using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Units;
using IntoChat.OS.Host.Kernel;
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
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;
using HostSafeRelativePath = IntoChat.OS.Host.Primitives.SafeRelativePath;

namespace IntoChat.OS.Modules.Flutter;

internal static class AppUiEndpoints
{
    private static readonly ILogger ConsoleLog = ShellConsoleLog.For(typeof(AppUiEndpoints));

    // Retained for test callers serializing view payloads with the same depth limit as ShellUiJsonContext.
    internal static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var logger = (endpoints.ServiceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
            ?.CreateLogger(typeof(AppUiEndpoints).FullName!)
            ?? ConsoleLog;
        var apps = BrainRouteGroups.Brain(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);
        apps.MapPost("/connect", async ([FromServices] IEnumerable<IUiRootProvider> providers, CancellationToken ct) =>
        {
            var provider = providers.SingleOrDefault();
            return OkJson(
                new ConnectResponse(provider is null ? null : await provider.Connect(ct), provider?.ActivationApp),
                ShellUiJsonContext.Default.ConnectResponse);
        });
        apps.MapGet("/desktop/projection", (HttpContext http, INeuronProxies brain, CancellationToken ct)
            => ReadDesktopProjection(brain, BrainScope.CurrentId(), ct, HttpCaller.Current(http)));
        apps.MapGet("/node", (HttpContext http, string kind, string name, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, [FromServices] IEnumerable<IScreenTargetResolver> resolvers, [FromServices] IEnumerable<IHostedNeuronTypes> hostedNeurons, CancellationToken ct)
            => ReadNode(kind, name, brain, BrainScope.CurrentId(), ct, logger, hosted.FirstOrDefault(), HttpCaller.Current(http), resolvers.FirstOrDefault(), hostedNeurons.FirstOrDefault()));
        apps.MapGet("/{name}/children/{**path}", (HttpContext http, string name, string path, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, [FromServices] IEnumerable<IScreenTargetResolver> resolvers, [FromServices] IEnumerable<IHostedNeuronTypes> hostedNeurons, CancellationToken ct)
            => ReadChild(name, path, brain, BrainScope.CurrentId(), ct, HttpCaller.Current(http), hosted.FirstOrDefault(), logger, resolvers.FirstOrDefault(), hostedNeurons.FirstOrDefault()));
        BrainRouteGroups.Brain(endpoints).AddEndpointFilter(ModuleRouteGuard.Guard).MapPost("/act", (HttpContext http, ActRequest input, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, [FromServices] IEnumerable<IScreenTargetResolver> resolvers, [FromServices] IEnumerable<IHostedNeuronTypes> hostedNeurons, CancellationToken ct)
            => Act(input, brain, BrainScope.CurrentId(), ct, HttpCaller.Current(http), hosted.FirstOrDefault(), logger, resolvers.FirstOrDefault(), hostedNeurons.FirstOrDefault()));
    }

    internal static async Task<IResult> ReadDesktopProjection(
        INeuronProxies brain,
        string scope,
        CancellationToken ct,
        DigitalBrain.Access.Caller? caller)
    {
        if (caller?.For?.Principal is not { Length: > 0 })
        {
            throw new UnauthorizedAccessException("Only a signed-in person reads the desktop projection.");
        }
        if (!BrainId.IsValid(scope) || caller.Brain != new BrainId(scope))
        {
            throw new UnauthorizedAccessException("The desktop projection belongs to another brain.");
        }
        var screenId = new NeuronId(new BrainId(scope), "app.desktop", IDesktopShell.DefaultKey);
        var projection = await brain.Get<IDesktopShell>(screenId).ReadProjection().WaitAsync(ct);
        return OkJson(projection, ShellUiJsonContext.Default.DesktopShellProjection);
    }

    // POST /act: one command to a neuron address, the only route a person's action takes onto a screen. The request names a
    // top-level screen: an app ({scope}/apps/{name}: a compiled screen, an agent app, a sandboxed app's view neuron, or the unit of
    // that alias) or a unit ({scope}/{alias}, {scope}/units/{alias}), resolved by ScreenViewResolver; and a target relative to it
    // (".", "key" or a key path "a/b"). The parent owns its children, so a child is reached only through the parent's Dispatch,
    // each hop refusing a key its current view does not show. A raw child id is not a screen name and is refused here.
    // Value is a field's text: it joins the args as "value". Args never name "by": the screen that handles the command stamps the person.
    // A node's client-local intent never comes here: the client performs it from the node.
    // Responses: 200 delivered; 400 a missing or malformed field, args naming "by", or a value the args already declare (checked before the handler runs; a handler's ArgumentException is 422); 409 {reason} a refusal (UnknownChild, NotLoaded, NoChildren);
    // 422 {error} the handler failed (Dispatch awaits it, so a child's failure reaches the client).
    internal static async Task<IResult> Act(ActRequest input, INeuronProxies brain, string scope, CancellationToken ct, DigitalBrain.Access.Caller? caller,
        IHostedAppTypes? hosted = null, ILogger? logger = null, IScreenTargetResolver? resolver = null, IHostedNeuronTypes? hostedNeurons = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (caller?.For?.Principal is not { Length: > 0 }) { throw new UnauthorizedAccessException("Only a signed-in person acts on a screen."); }
        if (string.IsNullOrWhiteSpace(input.Neuron) || string.IsNullOrWhiteSpace(input.Target) || string.IsNullOrWhiteSpace(input.Signal))
        {
            return ErrorJson(StatusCodes.Status400BadRequest, "An act names its neuron, target and signal.");
        }
        var unit = ScreenViewResolver.UnitAlias(scope, input.Neuron);
        if (unit is null && ScreenViewResolver.ScreenAlias(scope, input.Neuron) is null)
        {
            throw new UnauthorizedAccessException($"{input.Neuron} is not a screen of this brain.");
        }
        JsonElement? args;
        try
        {
            // Only the kernel names who acted (the handling screen stamps "by"), and a field's value never replaces one the command declares.
            ViewArgs.RequireUnreserved(input.Args, input.Value);
            args = input.Value is null ? input.Args : ViewArgs.WithValue(input.Args, input.Value);
        }
        catch (ArgumentException error)
        {
            return ErrorJson(StatusCodes.Status400BadRequest, error.Message);
        }

        var resolved = await ScreenViewResolver.ResolveAsync(new BrainId(scope), input.Neuron, resolver, hosted, ct, hostedNeurons);
        if (resolved is { Kind: ScreenResolutionKind.Confirmation })
        {
            throw new UnauthorizedAccessException("A confirmation is answered on its own route, not by a command.");
        }
        if (resolved is { Kind: ScreenResolutionKind.NotLoaded })
        {
            return ErrorJson(StatusCodes.Status409Conflict, $"{input.Neuron} is not loaded.", "NotLoaded");
        }
        if (resolved?.ScreenId is not { } screenId)
        {
            throw new UnauthorizedAccessException($"{input.Neuron} is not a screen of this brain.");
        }

        var screen = brain.Get<IScreen>(screenId);
        try
        {
            await screen.Dispatch(new DigitalBrain.UI.Command(input.Target, input.Signal, args)).WaitAsync(ct);
        }
        catch (ActivationRefusedException refused)
        {
            return ErrorJson(StatusCodes.Status409Conflict, $"{input.Target} of {input.Neuron} refused the command.", refused.Reason);
        }
        catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
        {
            (logger ?? ConsoleLog).LogWarning(error, "Act {Signal} on {Target} of {Neuron} failed.", input.Signal, input.Target, input.Neuron);
            return ErrorJson(StatusCodes.Status422UnprocessableEntity, error.Message, "HandlerFailed");
        }
        return OkJson(new ActDeliveredResponse(true), ShellUiJsonContext.Default.ActDeliveredResponse);
    }

    // GET /apps/{name}/children/{path}: the view of a UI child, reached as /act reaches it: through the top-level screen {scope}/apps/{name}
    // and its child table, one key per hop ("a/b"), never by the child's neuron id (which the response does not carry either: its own
    // children are named by key). Responses: 200 the view; 400 a malformed path; 404 {reason: UnknownChild} a key the view does not show;
    // 409 {reason} the app's host is not loaded (NotLoaded) or another refusal; 422 {reason: HandlerFailed} the read failed
    // (an ArgumentException from the handler included: 400 is only for the path checked before it runs).
    internal static async Task<IResult> ReadChild(string name, string path, INeuronProxies brain, string scope, CancellationToken ct, DigitalBrain.Access.Caller? caller,
        IHostedAppTypes? hosted = null, ILogger? logger = null, IScreenTargetResolver? resolver = null, IHostedNeuronTypes? hostedNeurons = null)
    {
        if (caller?.For?.Principal is not { Length: > 0 }) { throw new UnauthorizedAccessException("Only a signed-in person reads a screen."); }
        // The brain route admitted the caller to this brain; a caller acting in another brain never reads its screens.
        if (!BrainId.IsValid(scope) || caller.Brain != new BrainId(scope)) { throw new UnauthorizedAccessException("The screen belongs to another brain."); }
        var brainId = new BrainId(scope);
        if (!HostAppRef.TryOf(brainId, name, out var appRef) || !ScreenViewResolver.OwnsUi(scope, appRef.ViewKey))
        {
            throw new UnauthorizedAccessException($"{name} is not a screen of this brain.");
        }
        var neuron = appRef.Key;
        var alias = ScreenViewResolver.ScreenAlias(scope, neuron) ?? throw new UnauthorizedAccessException($"{neuron} is not a screen of this brain.");
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.StartsWith("./", StringComparison.Ordinal)
            || !HostSafeRelativePath.TryParse(path, out _))
        {
            return ErrorJson(StatusCodes.Status400BadRequest, "A child path is one or more keys separated by '/'.");
        }

        var resolved = await ScreenViewResolver.ResolveAsync(brainId, neuron, resolver, hosted, ct, hostedNeurons);
        NeuronId screenId;
        if (resolved is { Kind: ScreenResolutionKind.Screen, ScreenId: { } resolvedId })
        {
            screenId = resolvedId;
        }
        else if (resolved is { Kind: ScreenResolutionKind.NotLoaded }
            || (ScreenViewResolver.AppOf(scope, neuron) is { } appKey && !(hosted?.IsHostLoaded(appKey) ?? true)))
        {
            return ErrorJson(StatusCodes.Status409Conflict, $"{neuron} is not loaded.", "NotLoaded");
        }
        else
        {
            screenId = new NeuronId(brainId, alias, "default");
        }

        try
        {
            var view = await brain.Get<IScreen>(screenId).ReadChild(path).WaitAsync(ct);
            return OkJson(ChildKeys.Screen(view), ShellUiJsonContext.Default.PublishedView);
        }
        catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.UnknownChild)
        {
            return ErrorJson(StatusCodes.Status404NotFound, $"{neuron} shows no child '{path}'.", refused.Reason);
        }
        catch (ActivationRefusedException refused)
        {
            return ErrorJson(StatusCodes.Status409Conflict, $"{path} of {neuron} refused the read.", refused.Reason);
        }
        catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
        {
            (logger ?? ConsoleLog).LogWarning(error, "Reading child {Path} of {Neuron} failed.", path, neuron);
            return ErrorJson(StatusCodes.Status422UnprocessableEntity, error.Message, "HandlerFailed");
        }
    }

    // Dart mirror: NodeContext.act in ui/lib/src/nodes/node_context.dart.
    internal sealed record ActRequest(string? Neuron, string? Target, string? Signal, JsonElement? Args = null, string? Value = null);

    // GET /apps/node: one app UI node of the caller's brain, by kind.
    internal static async Task<IResult> ReadNode(string kind, string name, INeuronProxies brain, string scope, CancellationToken ct, ILogger? logger = null, IHostedAppTypes? hosted = null, DigitalBrain.Access.Caller? caller = null, IScreenTargetResolver? resolver = null, IHostedNeuronTypes? hostedNeurons = null)
    {
        var log = logger ?? ConsoleLog;
        if (kind == UIVocabulary.ConfirmationType)
        {
            if (name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
            return OkJson(
                ConfirmationView.Render(await brain.Get<IConfirmationInbox>(name).Read().WaitAsync(ct), caller),
                ShellUiJsonContext.Default.View);
        }
        var unit = kind is UIVocabulary.ViewType or "viewport" ? ScreenViewResolver.UnitAlias(scope, name) : null;
        if (unit is null && !ScreenViewResolver.OwnsUi(scope, name)) { throw new UnauthorizedAccessException("The UI belongs to another brain."); }
        var brainId = new BrainId(scope);

        if (kind == "viewport")
        {
            return await ReadViewportNodeAsync(name, unit, brain, brainId, scope, log, hosted, resolver, hostedNeurons, ct);
        }
        if (kind == UIVocabulary.ViewType)
        {
            return await ReadViewNodeAsync(name, unit, brain, brainId, scope, log, hosted, resolver, hostedNeurons, ct);
        }
        return kind switch
        {
            UIVocabulary.WebBrowserType => OkJson(await brain.Get<IWebBrowser>(name).Read().WaitAsync(ct), ShellUiJsonContext.Default.WebBrowserView),
            _ => throw new ArgumentException("Unknown app UI kind.")
        };
    }

    private static async Task<IResult> ReadViewportNodeAsync(
        string name,
        string? unit,
        INeuronProxies brain,
        BrainId brainId,
        string scope,
        ILogger log,
        IHostedAppTypes? hosted,
        IScreenTargetResolver? resolver,
        IHostedNeuronTypes? hostedNeurons,
        CancellationToken ct)
    {
        if (unit is not null)
        {
            var unitView = await ScreenViewResolver.ReadUnitViewAsync(brain, brainId, unit, log, ct);
            var failing = unitView is null ? null : await brain.Get<IDebuggerReads>(IDebuggerReads.Key(scope)).Failing(unit).WaitAsync(ct);
            return OkJson(
                new UnitViewportResponse(
                    unitView?.Generation ?? 0L,
                    Target: null,
                    Root: unitView is null ? null : new ViewRef(UIVocabulary.ViewType, new NeuronId(brainId, unit, unit).FullKey()),
                    Error: UnitError(unitView, failing)),
                ShellUiJsonContext.Default.UnitViewportResponse);
        }

        var viewport = await brain.Get<IViewport>(name).Read().WaitAsync(ct);
        if (viewport.Target is not { } target)
        {
            var directViewName = name.EndsWith("/view", StringComparison.Ordinal) ? name : $"{name}/view";
            if (ScreenViewResolver.OwnsUi(scope, directViewName))
            {
                if (ScreenViewResolver.ScreenAlias(scope, directViewName) is not null)
                {
                    var directResolved = await ScreenViewResolver.ResolveAsync(brainId, directViewName, resolver, hosted, ct, hostedNeurons);
                    if (directResolved is { Kind: ScreenResolutionKind.NotLoaded })
                    {
                        return OkJson(new EmptyViewportResponse(viewport.Revision, Root: null, Error: null), ShellUiJsonContext.Default.EmptyViewportResponse);
                    }
                    if (directResolved is { Kind: ScreenResolutionKind.Screen, ScreenId: { } directScreenId }
                        && await ScreenViewResolver.TryReadScreenAsync(brain, directScreenId, log, "Published screen view could not be read.", ct, hostedNeurons) is { } published)
                    {
                        return OkJson(
                            new UnitViewportResponse(published.Generation, Target: null, Root: new ViewRef(UIVocabulary.ViewType, directViewName), Error: null),
                            ShellUiJsonContext.Default.UnitViewportResponse);
                    }
                }
                var directView = await brain.Get<IView>(directViewName).Read().WaitAsync(ct);
                if (directView is not null)
                {
                    return OkJson(
                        new UnitViewportResponse(directView.Generation, Target: null, Root: new ViewRef(UIVocabulary.ViewType, directViewName), Error: null),
                        ShellUiJsonContext.Default.UnitViewportResponse);
                }
            }
            return OkJson(new EmptyViewportResponse(viewport.Revision, Root: null, Error: null), ShellUiJsonContext.Default.EmptyViewportResponse);
        }

        if (!ScreenViewResolver.OwnsUi(scope, target.App)) { throw new UnauthorizedAccessException(); }
        var appName = HostAppRef.TryParse(brainId, target.App, out var appRef)
            ? appRef.Name
            : target.App;
        if (!appName.Contains('/'))
        {
            var viewportResolved = await ScreenViewResolver.ResolveAsync(brainId, appName, resolver, hosted, ct, hostedNeurons);
            if (viewportResolved is { Kind: ScreenResolutionKind.NotLoaded })
            {
                return OkJson(
                    new TargetedViewportResponse(viewport.Revision, viewport.Target, Root: null, Error: "This app is not available in this runtime."),
                    ShellUiJsonContext.Default.TargetedViewportResponse);
            }
            if (viewportResolved is { Kind: ScreenResolutionKind.Screen, ScreenId: { } viewportScreenId }
                && await ScreenViewResolver.TryReadScreenAsync(brain, viewportScreenId, log, "Screen view could not be read.", ct, hostedNeurons) is not null)
            {
                return OkJson(
                    new TargetedViewportResponse(viewport.Revision, viewport.Target, new ViewRef(UIVocabulary.ViewType, HostAppRef.Of(brainId, appName).ViewKey), Error: null),
                    ShellUiJsonContext.Default.TargetedViewportResponse);
            }
            if (appRef != default && await brain.Get<IView>(appRef.ViewKey).Read().WaitAsync(ct) is not null)
            {
                return OkJson(
                    new TargetedViewportResponse(viewport.Revision, viewport.Target, new ViewRef(UIVocabulary.ViewType, appRef.ViewKey), Error: null),
                    ShellUiJsonContext.Default.TargetedViewportResponse);
            }
            if (viewportResolved is not { Kind: ScreenResolutionKind.Screen, IsAgentApp: false }
                && await ScreenViewResolver.TryReadUnitReaderAsync(brain, brainId, appName, log, "Unit view could not be read for viewport.", ct) is not null)
            {
                return OkJson(
                    new TargetedViewportResponse(viewport.Revision, viewport.Target, new ViewRef(UIVocabulary.ViewType, new NeuronId(brainId, "unit", appName).FullKey()), Error: null),
                    ShellUiJsonContext.Default.TargetedViewportResponse);
            }
        }

        return OkJson(
            new TargetedViewportResponse(viewport.Revision, viewport.Target, Root: null, Error: "This app has not published this view."),
            ShellUiJsonContext.Default.TargetedViewportResponse);
    }

    private static async Task<IResult> ReadViewNodeAsync(
        string name,
        string? unit,
        INeuronProxies brain,
        BrainId brainId,
        string scope,
        ILogger log,
        IHostedAppTypes? hosted,
        IScreenTargetResolver? resolver,
        IHostedNeuronTypes? hostedNeurons,
        CancellationToken ct)
    {
        if (unit is not null)
        {
            var unitView = await ScreenViewResolver.ReadUnitViewAsync(brain, brainId, unit, log, ct);
            return OkJson(ChildKeys.Hide(unitView), ShellUiJsonContext.Default.PublishedView);
        }

        if (ScreenViewResolver.ScreenAlias(scope, name) is { } screenAlias)
        {
            var resolved = await ScreenViewResolver.ResolveAsync(brainId, name, resolver, hosted, ct, hostedNeurons);
            if (resolved is { Kind: ScreenResolutionKind.NotLoaded })
            {
                return OkJson(null, ShellUiJsonContext.Default.PublishedView);
            }
            if (resolved is { Kind: ScreenResolutionKind.Screen, ScreenId: { } viewScreenId })
            {
                if (await ScreenViewResolver.TryReadScreenAsync(brain, viewScreenId, log, "Screen view could not be read.", ct, hostedNeurons) is { } published)
                {
                    return OkJson(ChildKeys.Screen(published), ShellUiJsonContext.Default.PublishedView);
                }
            }
            else if (await brain.Get<IView>(name).Read().WaitAsync(ct) is { } directAppView)
            {
                return OkJson(ChildKeys.Hide(directAppView), ShellUiJsonContext.Default.PublishedView);
            }

            if (resolved is not { Kind: ScreenResolutionKind.Screen, IsAgentApp: false })
            {
                var candidateUnit = screenAlias["app.".Length..];
                if (await ScreenViewResolver.TryReadUnitReaderAsync(brain, brainId, candidateUnit, log, "Unit view fallback could not be read.", ct) is { } unitFallback)
                {
                    return OkJson(ChildKeys.Hide(unitFallback), ShellUiJsonContext.Default.PublishedView);
                }
            }
        }

        return OkJson(ChildKeys.Hide(await brain.Get<IView>(name).Read().WaitAsync(ct)), ShellUiJsonContext.Default.PublishedView);
    }

    private static IResult OkJson<T>(T? value, JsonTypeInfo<T> typeInfo)
        => Results.Json(value, typeInfo, statusCode: StatusCodes.Status200OK);

    private static IResult ErrorJson(int statusCode, string error, string? reason = null)
        => Results.Json(new UiErrorResponse(error, reason), ShellUiJsonContext.Default.UiErrorResponse, statusCode: statusCode);

    internal static string? ScreenAlias(string scope, string name) => ScreenViewResolver.ScreenAlias(scope, name);

    internal static string? UnitAlias(string scope, string name) => ScreenViewResolver.UnitAlias(scope, name);

    internal static bool OwnsUi(string scope, string name) => ScreenViewResolver.OwnsUi(scope, name);

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
        var payload = joined is { ValueKind: JsonValueKind.Object } given ? JsonObject.Create(given) ?? [] : new JsonObject();
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
            var items = JsonArray.Create(array) ?? [];
            items.Add((JsonNode?)JsonValue.Create(value));
            using var arrDoc = JsonDocument.Parse(items.ToJsonString());
            return arrDoc.RootElement.Clone();
        }
        var payload = args is { ValueKind: JsonValueKind.Object } given ? JsonObject.Create(given) ?? [] : new JsonObject();
        foreach (var existing in payload.Select(kv => kv.Key).Where(k => string.Equals(k, Value, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            payload.Remove(existing);
        }
        if (args is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) } other) { payload["args"] = JsonNode.Parse(other.GetRawText()); }
        payload["value"] = value;
        using var objDoc = JsonDocument.Parse(payload.ToJsonString());
        return objDoc.RootElement.Clone();
    }
}
