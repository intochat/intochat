using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Confirmation;
using DigitalBrain.Errors;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MicrosoftSdk = Microsoft;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Flutter;

internal static class AppUiEndpoints
{
    private static readonly ILogger ConsoleLog = ShellConsoleLog.For(typeof(AppUiEndpoints));

    // GET /apps/node?kind=view writes the tree with these options; the other kinds use the host's HTTP JSON options.
    internal static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var logger = (endpoints.ServiceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
            ?.CreateLogger(typeof(AppUiEndpoints).FullName!)
            ?? ConsoleLog;
        var apps = OsBrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);
        apps.MapPost("/connect", async ([FromServices] IEnumerable<IUiRootProvider> providers, CancellationToken ct) =>
        {
            var provider = providers.SingleOrDefault();
            return Results.Ok(new { Root = provider is null ? null : await provider.Connect(ct), provider?.ActivationApp });
        });
        apps.MapGet("/node", (HttpContext http, string kind, string name, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, [FromServices] IEnumerable<IScreenTargetResolver> resolvers, CancellationToken ct)
            => ReadNode(kind, name, brain, BrainScope.CurrentId(), ct, logger, hosted.FirstOrDefault(), HttpCaller.Current(http), resolvers.FirstOrDefault()));
        apps.MapGet("/{name}/slots/{**path}", (HttpContext http, string name, string path, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, [FromServices] IEnumerable<IScreenTargetResolver> resolvers, CancellationToken ct)
            => ReadChild(name, path, brain, BrainScope.CurrentId(), ct, HttpCaller.Current(http), hosted.FirstOrDefault(), logger, resolvers.FirstOrDefault()));
        OsBrainRoutes.Group(endpoints).AddEndpointFilter(ModuleRouteGuard.Guard).MapPost("/act", (HttpContext http, ActRequest input, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, [FromServices] IEnumerable<IScreenTargetResolver> resolvers, CancellationToken ct)
            => Act(input, brain, BrainScope.CurrentId(), ct, HttpCaller.Current(http), hosted.FirstOrDefault(), logger, resolvers.FirstOrDefault()));

    }

    // POST /act: one command to a neuron address, the only route a person's action takes onto a screen. The request names a
    // top-level screen: an app ({scope}/apps/{name}: a compiled screen, an agent app, a sandboxed app's view neuron, or the unit of
    // that alias) or a unit ({scope}/{alias}, {scope}/units/{alias}), resolved by IScreenTargetResolver; and a target relative to it
    // ("." , "key" or a key path "a/b"). The parent owns its children, so a child is reached only through the parent's Dispatch,
    // each hop refusing a key its current view does not show. A raw child id is not a screen name and is refused here.
    // Value is a field's text: it joins the args as "value". Args never name "by": the screen that handles the command stamps the person.
    // A node's client-local intent never comes here: the client performs it from the node.
    // Responses: 200 delivered; 400 a missing or malformed field, args naming "by", or a value the args already declare (checked before the handler runs; a handler's ArgumentException is 422); 409 {reason} a refusal (UnknownChild, NotLoaded, NoSlots);
    // 422 {error} the handler failed (Dispatch awaits it, so a child's failure reaches the client).
    internal static async Task<IResult> Act(ActRequest input, INeuronProxies brain, string scope, CancellationToken ct, DigitalBrain.Access.Caller? caller,
        IHostedAppTypes? hosted = null, ILogger? logger = null, IScreenTargetResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (caller?.For?.Principal is not { Length: > 0 }) { throw new UnauthorizedAccessException("Only a signed-in person acts on a screen."); }
        if (string.IsNullOrWhiteSpace(input.Neuron) || string.IsNullOrWhiteSpace(input.Target) || string.IsNullOrWhiteSpace(input.Signal))
        {
            return Results.BadRequest(new { error = "An act names its neuron, target and signal." });
        }
        var unit = UnitAlias(scope, input.Neuron);
        var alias = unit is null ? ScreenAlias(scope, input.Neuron) ?? throw new UnauthorizedAccessException($"{input.Neuron} is not a screen of this brain.") : null;
        JsonElement? args;
        try
        {
            // Only the kernel names who acted (the handling screen stamps "by"), and a field's value never replaces one the command declares.
            ViewArgs.RequireUnreserved(input.Args, input.Value);
            args = input.Value is null ? input.Args : ViewArgs.WithValue(input.Args, input.Value);
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }
        var resolvedTarget = await ResolveScreenTargetAsync(resolver, scope, input.Neuron, ct);
        NeuronId screenId;
        if (resolvedTarget is { Kind: IScreenTargetResolver.ConfirmationsKind })
        {
            throw new UnauthorizedAccessException("A confirmation is answered on its own route, not by a command.");
        }
        else if (resolvedTarget is not null)
        {
            screenId = resolvedTarget.ScreenId;
        }
        else if (unit is not null)
        {
            screenId = UnitState(scope, unit);
        }
        else if (AppOf(scope, input.Neuron.EndsWith("/view", StringComparison.Ordinal) ? input.Neuron[..^5] : input.Neuron) is { } appKey
            && !(hosted?.IsHostLoaded(appKey) ?? true))
        {
            return Results.Conflict(new { error = $"{input.Neuron} is not loaded.", reason = "NotLoaded" });
        }
        else
        {
            screenId = new NeuronId(new BrainId(scope), alias!, "default");
        }
        var screen = brain.Get<IScreen>(screenId);
        try
        {
            await screen.Dispatch(new DigitalBrain.UI.Command(input.Target, input.Signal, args)).WaitAsync(ct);
        }
        catch (ActivationRefusedException refused)
        {
            return Results.Conflict(new { error = $"{input.Target} of {input.Neuron} refused the command.", reason = refused.Reason });
        }
        catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
        {
            (logger ?? ConsoleLog).LogWarning(error, "Act {Signal} on {Target} of {Neuron} failed.", input.Signal, input.Target, input.Neuron);
            return Results.UnprocessableEntity(new { error = error.Message, reason = "HandlerFailed" });
        }
        return Results.Ok(new { delivered = true });
    }

    // GET /apps/{name}/slots/{path}: the view of a UI child, reached as /act reaches it: through the top-level screen {scope}/apps/{name}
    // and its child table, one key per hop ("a/b"), never by the child's neuron id (which the response does not carry either: its own
    // children are named by key). Responses: 200 the view; 400 a malformed path; 404 {reason: UnknownChild} a key the view does not show;
    // 409 {reason} the app's host is not loaded (NotLoaded) or another refusal; 422 {reason: HandlerFailed} the read failed
    // (an ArgumentException from the handler included: 400 is only for the path checked before it runs).
    internal static async Task<IResult> ReadChild(string name, string path, INeuronProxies brain, string scope, CancellationToken ct, DigitalBrain.Access.Caller? caller,
        IHostedAppTypes? hosted = null, ILogger? logger = null, IScreenTargetResolver? resolver = null)
    {
        if (caller?.For?.Principal is not { Length: > 0 }) { throw new UnauthorizedAccessException("Only a signed-in person reads a screen."); }
        // The brain route admitted the caller to this brain; a caller acting in another brain never reads its screens.
        if (!BrainId.IsValid(scope) || caller.Brain != new BrainId(scope)) { throw new UnauthorizedAccessException("The screen belongs to another brain."); }
        if (!IntoChat.OS.Host.Primitives.AppRef.TryOf(new BrainId(scope), name, out var appRef) || !OwnsUi(scope, appRef.ViewKey))
        {
            throw new UnauthorizedAccessException($"{name} is not a screen of this brain.");
        }
        var neuron = appRef.Key;
        var alias = ScreenAlias(scope, neuron) ?? throw new UnauthorizedAccessException($"{neuron} is not a screen of this brain.");
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.StartsWith("./", StringComparison.Ordinal)
            || !IntoChat.OS.Host.Primitives.SafeRelativePath.TryParse(path, out _))
        {
            return Results.BadRequest(new { error = "A slot path is one or more keys separated by '/'." });
        }
        var resolvedTarget = await ResolveScreenTargetAsync(resolver, scope, neuron, ct);
        NeuronId screenId;
        if (resolvedTarget is { Kind: IScreenTargetResolver.CompiledScreenKind or IScreenTargetResolver.AgentAppKind })
        {
            screenId = resolvedTarget.ScreenId;
        }
        else if (AppOf(scope, neuron) is { } appKey && !(hosted?.IsHostLoaded(appKey) ?? true))
        {
            return Results.Conflict(new { error = $"{neuron} is not loaded.", reason = "NotLoaded" });
        }
        else
        {
            screenId = new NeuronId(new BrainId(scope), alias, "default");
        }
        try
        {
            var view = await brain.Get<IScreen>(screenId).ReadChild(path).WaitAsync(ct);
            return Results.Json(SlotKeys.Screen(view), ViewJson);
        }
        catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.UnknownChild)
        {
            return Results.NotFound(new { error = $"{neuron} shows no slot '{path}'.", reason = refused.Reason });
        }
        catch (ActivationRefusedException refused)
        {
            return Results.Conflict(new { error = $"{path} of {neuron} refused the read.", reason = refused.Reason });
        }
        catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
        {
            (logger ?? ConsoleLog).LogWarning(error, "Reading slot {Path} of {Neuron} failed.", path, neuron);
            return Results.UnprocessableEntity(new { error = error.Message, reason = "HandlerFailed" });
        }
    }

    // Dart mirror: NodeContext.act in ui/lib/src/nodes/node_context.dart.
    internal sealed record ActRequest(string? Neuron, string? Target, string? Signal, JsonElement? Args = null, string? Value = null);

    // GET /apps/node: one app UI node of the caller's brain, by kind.
    internal static async Task<IResult> ReadNode(string kind, string name, INeuronProxies brain, string scope, CancellationToken ct, ILogger? logger = null, IHostedAppTypes? hosted = null, DigitalBrain.Access.Caller? caller = null, IScreenTargetResolver? resolver = null)
    {
        var log = logger ?? ConsoleLog;
        if (kind == UIVocabulary.ConfirmationType)
        {
            if (name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
            return Results.Json(ConfirmationView.Render(await brain.Get<IConfirmationInbox>(name).Read().WaitAsync(ct), caller), ViewJson);
        }
        var unit = kind is UIVocabulary.ViewType or "viewport" ? UnitAlias(scope, name) : null;
        if (unit is null && !OwnsUi(scope, name)) { throw new UnauthorizedAccessException("The UI belongs to another brain."); }
        if (kind == "viewport")
        {
            if (unit is not null)
            {
                var unitView = await brain.Get<IViewReader>(UnitState(scope, unit)).Read(unit).WaitAsync(ct);
                if (unitView is null)
                {
                    try
                    {
                        var screenId = new NeuronId(new BrainId(scope), unit, "default");
                        unitView = await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
                    }
                    catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
                    {
                        log.LogSwallowed(error, "Screen view for unit could not be read.");
                    }
                }
                var failing = unitView is null ? null : await brain.Get<IDebuggerReads>(IDebuggerReads.Key(scope)).Failing(unit).WaitAsync(ct);
                return Results.Ok(new
                {
                    Revision = unitView?.Generation ?? 0L,
                    Target = (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef?)null,
                    Root = unitView is null ? null : new ViewRef(UIVocabulary.ViewType, new NeuronId(new BrainId(scope), unit, unit).FullKey()),
                    Error = UnitError(unitView, failing),
                });
            }
            var viewport = await brain.Get<IViewport>(name).Read().WaitAsync(ct);
            if (viewport.Target is not { } target)
            {
                var directViewName = name.EndsWith("/view", StringComparison.Ordinal) ? name : $"{name}/view";
                if (OwnsUi(scope, directViewName))
                {
                    if (ScreenAlias(scope, directViewName) is { } directScreenAlias)
                    {
                        var directAppKey = AppOf(scope, directViewName);
                        var directTarget = directAppKey is not null ? await ResolveScreenTargetAsync(resolver, scope, directAppKey, ct) : null;
                        var isDirectHostLoaded = directAppKey is null || (hosted?.IsHostLoaded(directAppKey) ?? true);
                        NeuronId? directScreenId = directTarget is { Kind: IScreenTargetResolver.CompiledScreenKind or IScreenTargetResolver.AgentAppKind }
                            ? directTarget.ScreenId
                            : isDirectHostLoaded
                                ? new NeuronId(new BrainId(scope), directScreenAlias, "default")
                                : null;
                        if (directScreenId is { } resolvedDirectScreenId)
                        {
                            try
                            {
                                var published = await brain.Get<IScreen>(resolvedDirectScreenId).Read().WaitAsync(ct);
                                if (published is not null)
                                {
                                    return Results.Ok(new
                                    {
                                        Revision = published.Generation,
                                        Target = (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef?)null,
                                        Root = new ViewRef(UIVocabulary.ViewType, directViewName),
                                        Error = (string?)null,
                                    });
                                }
                            }
                            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                            {
                                log.LogSwallowed(error, "Published screen view could not be read.");
                            }
                        }
                    }
                    var directView = await brain.Get<IView>(directViewName).Read().WaitAsync(ct);
                    if (directView is not null)
                    {
                        return Results.Ok(new
                        {
                            Revision = directView.Generation,
                            Target = (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef?)null,
                            Root = new ViewRef(UIVocabulary.ViewType, directViewName),
                            Error = (string?)null,
                        });
                    }
                }
                return Results.Ok(new { viewport.Revision, Root = (ViewRef?)null, Error = (string?)null });
            }
            if (!OwnsUi(scope, target.App)) { throw new UnauthorizedAccessException(); }
            var brainId = new BrainId(scope);
            var appName = IntoChat.OS.Host.Primitives.AppRef.TryParse(brainId, target.App, out var appRef)
                ? appRef.Name
                : target.App;
            var isHostLoaded = appRef == default || (hosted?.IsHostLoaded(appRef.Key) ?? true);
            if (!appName.Contains('/'))
            {
                var viewportTarget = await ResolveScreenTargetAsync(resolver, scope, appName, ct);
                NeuronId? viewportScreenId = viewportTarget is { Kind: IScreenTargetResolver.CompiledScreenKind or IScreenTargetResolver.AgentAppKind }
                    ? viewportTarget.ScreenId
                    : isHostLoaded
                        ? (IntoChat.OS.Host.Primitives.UnitAlias.TryParse(appName, out var screenUnit)
                            ? screenUnit.ScreenNeuron(brainId)
                            : new NeuronId(brainId, "app." + appName, "default"))
                        : null;
                if (viewportScreenId is { } resolvedViewportScreenId)
                {
                    try
                    {
                        var published = await brain.Get<IScreen>(resolvedViewportScreenId).Read().WaitAsync(ct);
                        if (published is not null)
                        {
                            return Results.Ok(new
                            {
                                viewport.Revision,
                                viewport.Target,
                                Root = new ViewRef(UIVocabulary.ViewType, IntoChat.OS.Host.Primitives.AppRef.Of(brainId, appName).ViewKey),
                                Error = (string?)null,
                            });
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                    {
                        log.LogSwallowed(error, "Screen view could not be read.");
                    }
                }
                if (appRef != default && await brain.Get<IView>(appRef.ViewKey).Read().WaitAsync(ct) is not null)
                {
                    return Results.Ok(new
                    {
                        viewport.Revision,
                        viewport.Target,
                        Root = new ViewRef(UIVocabulary.ViewType, appRef.ViewKey),
                        Error = (string?)null,
                    });
                }
                try
                {
                    var unitPublished = await brain.Get<IViewReader>(UnitState(scope, appName)).Read(appName).WaitAsync(ct);
                    if (unitPublished is not null)
                    {
                        return Results.Ok(new
                        {
                            viewport.Revision,
                            viewport.Target,
                            Root = new ViewRef(UIVocabulary.ViewType, new NeuronId(brainId, "unit", appName).FullKey()),
                            Error = (string?)null,
                        });
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                {
                    log.LogSwallowed(error, "Unit view could not be read for viewport.");
                }
            }
            var app = await brain.Get<IApp>(target.App).Read().WaitAsync(ct);
            var presentation = app.Presentation;
            var root = presentation?.Root?.Views?.FirstOrDefault(view => view.Id == target.Variant)?.Root;
            if (root is not null && !OwnsUi(scope, root.Name)) { throw new UnauthorizedAccessException(); }
            return Results.Ok(new
            {
                viewport.Revision,
                viewport.Target,
                Root = root,
                Error = presentation?.Error ?? (root is null ? "This app has not published this view." : null)
            });
        }
        if (kind == UIVocabulary.ViewType)
        {
            if (unit is not null)
            {
                var unitView = await brain.Get<IViewReader>(UnitState(scope, unit)).Read(unit).WaitAsync(ct);
                if (unitView is null)
                {
                    try
                    {
                        var screenId = new NeuronId(new BrainId(scope), unit, "default");
                        unitView = await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
                    }
                    catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
                    {
                        log.LogSwallowed(error, "Screen view for unit could not be read.");
                    }
                }
                return Results.Json(SlotKeys.Hide(unitView), ViewJson);
            }
            if (ScreenAlias(scope, name) is { } screenAlias)
            {
                var appKey = AppOf(scope, name);
                var resolvedTarget = appKey is not null ? await ResolveScreenTargetAsync(resolver, scope, appKey, ct) : null;
                var isHostLoaded = appKey is null || (hosted?.IsHostLoaded(appKey) ?? true);
                NeuronId? viewScreenId = resolvedTarget is { Kind: IScreenTargetResolver.CompiledScreenKind or IScreenTargetResolver.AgentAppKind }
                    ? resolvedTarget.ScreenId
                    : isHostLoaded
                        ? new NeuronId(new BrainId(scope), screenAlias, "default")
                        : null;
                if (viewScreenId is { } resolvedViewScreenId)
                {
                    try
                    {
                        var published = await brain.Get<IScreen>(resolvedViewScreenId).Read().WaitAsync(ct);
                        if (published is not null)
                        {
                            return Results.Json(SlotKeys.Screen(published), ViewJson);
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                    {
                        log.LogSwallowed(error, "Screen view could not be read.");
                    }
                }
                else if (await brain.Get<IView>(name).Read().WaitAsync(ct) is { } directAppView)
                {
                    return Results.Json(SlotKeys.Hide(directAppView), ViewJson);
                }
                var candidateUnit = screenAlias["app.".Length..];
                try
                {
                    var unitFallback = await brain.Get<IViewReader>(UnitState(scope, candidateUnit)).Read(candidateUnit).WaitAsync(ct);
                    if (unitFallback is not null)
                    {
                        return Results.Json(SlotKeys.Hide(unitFallback), ViewJson);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                {
                    log.LogSwallowed(error, "Unit view fallback could not be read.");
                }
            }
            return Results.Json(SlotKeys.Hide(await brain.Get<IView>(name).Read().WaitAsync(ct)), ViewJson);
        }
        return kind switch
        {
            UIVocabulary.WebBrowserType => Results.Ok(await brain.Get<IWebBrowser>(name).Read().WaitAsync(ct)),
            _ => throw new ArgumentException("Unknown app UI kind.")
        };
    }

    private static Task<ScreenTarget?> ResolveScreenTargetAsync(
        IScreenTargetResolver? resolver,
        string scope,
        string appName,
        CancellationToken ct)
        => resolver is not null && BrainId.IsValid(scope)
            ? resolver.ResolveAsync(new BrainId(scope), appName, ct)
            : Task.FromResult<ScreenTarget?>(null);

    // The view's owning app, {scope}/apps/{name}.
    private static string? AppOf(string scope, string view)
    {
        if (!BrainId.IsValid(scope)) { return null; }
        var brainId = new BrainId(scope);
        if (IntoChat.OS.Host.Primitives.AppRef.TryParse(brainId, view, out var direct)) { return direct.Key; }
        var parts = view.Split('/');
        return parts.Length > 3
            && parts[0] == scope
            && parts[1] == "apps"
            && IntoChat.OS.Host.Primitives.AppRef.TryOf(brainId, parts[2], out var owning)
            ? owning.Key
            : null;
    }

    internal static string? ScreenAlias(string scope, string name)
    {
        if (!BrainId.IsValid(scope)) { return null; }
        var brainId = new BrainId(scope);
        var withoutView = name.EndsWith("/view", StringComparison.Ordinal) ? name[..^5] : name;
        return IntoChat.OS.Host.Primitives.AppRef.TryParse(brainId, withoutView, out var app)
            ? "app." + app.Name
            : null;
    }

    // A unit's view is named {scope}/{alias}: one segment, a state alias rather than an app area,
    // or {scope}/units/{alias}, optionally ending in /view.
    internal static string? UnitAlias(string scope, string name) =>
        name.StartsWith(scope + "/", StringComparison.Ordinal)
        && BrainId.IsValid(scope)
        && IntoChat.OS.Host.Primitives.UnitAlias.TryFromAddress(new BrainId(scope), name, out var alias)
            ? alias.Value
            : null;

    // The alias's state neuron, {scope}/{alias}/default.
    private static NeuronId UnitState(string scope, string alias) =>
        IntoChat.OS.Host.Primitives.UnitAlias.TryParse(alias, out var unit)
            ? unit.StateNeuron(new BrainId(scope))
            : new(new BrainId(scope), alias, "default");

    internal static string? UnitError(PublishedView? view, TraceEntry? failing)
    {
        if (view is null) { return "This unit has not published a view."; }
        if (failing is null) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(failing.Payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var function = doc.RootElement.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "" : "";
                var dot = function.LastIndexOf('.');
                var method = dot >= 0 ? function[(dot + 1)..] : function;
                var message = doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() ?? "" : "";
                return method.Length == 0 ? "The last reaction failed." : $"{method} failed: {message}";
            }
            return "The last reaction failed.";
        }
        catch (JsonException)
        {
            return "The last reaction failed.";
        }
    }

    internal static bool OwnsUi(string scope, string name) =>
        (name.StartsWith(scope + "/", StringComparison.Ordinal)
         && name.Split('/') is [_, "apps" or "images" or "applications" or "units", ..])
        || UnitAlias(scope, name) is not null;
}

// An activation's field value joins the command's args as "value" (an object's member; other args become {args, value}).
internal static class ViewArgs
{
    // The members only the edge may set: "by" names who acted (no view may declare one), and "value" is the activated field's text
    // (a view that declares one beside a field would be overwritten silently, so it is refused).
    public const string By = "by";
    public const string Value = "value";

    public static void RequireUnreserved(JsonElement? args, string? value)
    {
        if (args is not { ValueKind: JsonValueKind.Object } given) { return; }
        foreach (var member in given.EnumerateObject())
        {
            if (string.Equals(member.Name, By, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A view's command cannot declare \"by\": only the edge names who acted."); }
            if (value is not null && member.Value.ValueKind != JsonValueKind.Null && string.Equals(member.Name, Value, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A view's command already declares \"value\"; a field's text cannot replace it."); }
        }
    }

    // The args a view neuron sends its app: the field value joined (WithValue) and, when the args are an object or none, the acting
    // person under "by" in place of any declared one. A positional array or a scalar has no member to carry the person.
    public static JsonElement Stamped(JsonElement? args, string? value, string? by)
    {
        var joined = value is null ? args : WithValue(args, value);
        if (joined is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) } positional) { return positional; }
        var payload = joined is { ValueKind: JsonValueKind.Object } given ? System.Text.Json.Nodes.JsonObject.Create(given) ?? [] : new System.Text.Json.Nodes.JsonObject();
        foreach (var declared in payload.Select(kv => kv.Key).Where(k => string.Equals(k, By, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            payload.Remove(declared);
        }
        if (by is not null) { payload[By] = by; }
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        return doc.RootElement.Clone();
    }

    // An object gets a "value" member; an array gets the value appended (a positional last argument); a scalar or no args
    // become {args?, value}. Args that already declare a reserved member are refused.
    public static JsonElement WithValue(JsonElement? args, string value)
    {
        RequireUnreserved(args, value);
        if (args is { ValueKind: JsonValueKind.Array } array)
        {
            var items = System.Text.Json.Nodes.JsonArray.Create(array) ?? [];
            items.Add(value);
            using var arrDoc = JsonDocument.Parse(items.ToJsonString());
            return arrDoc.RootElement.Clone();
        }
        var payload = args is { ValueKind: JsonValueKind.Object } given ? System.Text.Json.Nodes.JsonObject.Create(given) ?? [] : new System.Text.Json.Nodes.JsonObject();
        foreach (var existing in payload.Select(kv => kv.Key).Where(k => string.Equals(k, Value, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            payload.Remove(existing);
        }
        if (args is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) } other) { payload["args"] = System.Text.Json.Nodes.JsonNode.Parse(other.GetRawText()); }
        payload["value"] = value;
        using var objDoc = JsonDocument.Parse(payload.ToJsonString());
        return objDoc.RootElement.Clone();
    }
}
