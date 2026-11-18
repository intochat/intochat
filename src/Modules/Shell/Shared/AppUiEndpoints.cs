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
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Fields;
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
        apps.MapGet("/node", (HttpContext http, string kind, string name, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, CancellationToken ct)
            => ReadNode(kind, name, brain, BrainScope.CurrentId(), ct, logger, hosted.FirstOrDefault(), HttpCaller.Current(http)));
        apps.MapPost("/event", (HttpContext http, UiEvent input, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, CancellationToken ct)
            => Deliver(input, brain, BrainScope.CurrentId(), hosted.FirstOrDefault(), ct, HttpCaller.Current(http), logger: logger));
        OsBrainRoutes.Group(endpoints).AddEndpointFilter(ModuleRouteGuard.Guard).MapPost("/act", (HttpContext http, ActRequest input, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, CancellationToken ct)
            => Act(input, brain, BrainScope.CurrentId(), ct, HttpCaller.Current(http), hosted.FirstOrDefault(), logger));

    }

    // POST /act: one command to a neuron address. The request names a top-level screen ({scope}/apps/{name}) and a target relative
    // to it ("." , "key" or a key path "a/b"); the parent owns its children, so a child is reached only through the parent's
    // Dispatch, each hop refusing a key its current view does not show. A raw child id is not a screen name and is refused here.
    // Responses: 200 delivered; 400 a missing or malformed field; 409 {reason} a refusal (UnknownChild, NotLoaded);
    // 422 {error} the handler failed (Dispatch awaits it, so a child's failure reaches the client).
    internal static async Task<IResult> Act(ActRequest input, INeuronProxies brain, string scope, CancellationToken ct, DigitalBrain.Access.Caller? caller,
        IHostedAppTypes? hosted = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (caller?.For?.Principal is not { Length: > 0 }) { throw new UnauthorizedAccessException("Only a signed-in person acts on a screen."); }
        if (string.IsNullOrWhiteSpace(input.Neuron) || string.IsNullOrWhiteSpace(input.Target) || string.IsNullOrWhiteSpace(input.Signal))
        {
            return Results.BadRequest(new { error = "An act names its neuron, target and signal." });
        }
        var alias = ScreenAlias(scope, input.Neuron) ?? throw new UnauthorizedAccessException($"{input.Neuron} is not a screen of this brain.");
        var appKey = AppOf(scope, input.Neuron.EndsWith("/view", StringComparison.Ordinal) ? input.Neuron[..^5] : input.Neuron);
        if (appKey is not null && !(hosted?.IsHostLoaded(appKey) ?? true))
        {
            return Results.Conflict(new { error = $"{input.Neuron} is not loaded.", reason = "NotLoaded" });
        }
        var screen = brain.Get<IScreen>(new NeuronId(new BrainId(scope), alias, "default"));
        try
        {
            await screen.Dispatch(new DigitalBrain.UI.Command(input.Target, input.Signal, input.Args)).WaitAsync(ct);
        }
        catch (ActivationRefusedException refused)
        {
            return Results.Conflict(new { error = $"{input.Target} of {input.Neuron} refused the command.", reason = refused.Reason });
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }
        catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
        {
            (logger ?? ConsoleLog).LogWarning(error, "Act {Signal} on {Target} of {Neuron} failed.", input.Signal, input.Target, input.Neuron);
            return Results.UnprocessableEntity(new { error = error.Message, reason = "HandlerFailed" });
        }
        return Results.Ok(new { delivered = true });
    }

    // Dart mirror: NodeContext.act in ui/lib/src/nodes/node_context.dart.
    internal sealed record ActRequest(string? Neuron, string? Target, string? Signal, JsonElement? Args = null);

    // GET /apps/node: one app UI node of the caller's brain, by kind.
    internal static async Task<IResult> ReadNode(string kind, string name, INeuronProxies brain, string scope, CancellationToken ct, ILogger? logger = null, IHostedAppTypes? hosted = null, DigitalBrain.Access.Caller? caller = null)
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
                    Root = unitView is null ? null : new ViewRef(UIVocabulary.ViewType, $"{scope}/{unit}"),
                    Error = UnitError(unitView, failing),
                });
            }
            var viewport = await brain.Get<IViewport>(name).Read().WaitAsync(ct);
            if (viewport.Target is not { } target)
            {
                var directViewName = name.EndsWith("/view", StringComparison.Ordinal) ? name : $"{name}/view";
                if (OwnsUi(scope, directViewName))
                {
                    var directAppKey = AppOf(scope, directViewName);
                    var isDirectHostLoaded = directAppKey is null || (hosted?.IsHostLoaded(directAppKey) ?? true);
                    if (isDirectHostLoaded && ScreenAlias(scope, directViewName) is { } directScreenAlias)
                    {
                        try
                        {
                            var screenId = new NeuronId(new BrainId(scope), directScreenAlias, "default");
                            var published = await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
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
            var appName = IntoChat.OS.Primitives.AppRef.TryParse(brainId, target.App, out var appRef)
                ? appRef.Name
                : target.App;
            var isHostLoaded = appRef == default || (hosted?.IsHostLoaded(appRef.Key) ?? true);
            if (!appName.Contains('/'))
            {
                if (isHostLoaded)
                {
                    try
                    {
                        var screenId = IntoChat.OS.Primitives.UnitAlias.TryParse(appName, out var screenUnit)
                            ? screenUnit.ScreenNeuron(brainId)
                            : new NeuronId(brainId, "app." + appName, "default");
                        var published = await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
                        if (published is not null)
                        {
                            return Results.Ok(new
                            {
                                viewport.Revision,
                                viewport.Target,
                                Root = new ViewRef(UIVocabulary.ViewType, IntoChat.OS.Primitives.AppRef.Of(brainId, appName).ViewKey),
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
                            Root = new ViewRef(UIVocabulary.ViewType, $"{scope}/{appName}"),
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
                return Results.Json(unitView, ViewJson);
            }
            if (ScreenAlias(scope, name) is { } screenAlias)
            {
                var appKey = AppOf(scope, name);
                var isHostLoaded = appKey is null || (hosted?.IsHostLoaded(appKey) ?? true);
                if (isHostLoaded)
                {
                    try
                    {
                        var screenId = new NeuronId(new BrainId(scope), screenAlias, "default");
                        var published = await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
                        if (published is not null)
                        {
                            return Results.Json(published, ViewJson);
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                    {
                        log.LogSwallowed(error, "Screen view could not be read.");
                    }
                }
                else if (await brain.Get<IView>(name).Read().WaitAsync(ct) is { } directAppView)
                {
                    return Results.Json(directAppView, ViewJson);
                }
                var candidateUnit = screenAlias["app.".Length..];
                try
                {
                    var unitFallback = await brain.Get<IViewReader>(UnitState(scope, candidateUnit)).Read(candidateUnit).WaitAsync(ct);
                    if (unitFallback is not null)
                    {
                        return Results.Json(unitFallback, ViewJson);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                {
                    log.LogSwallowed(error, "Unit view fallback could not be read.");
                }
            }
            return Results.Json(await brain.Get<IView>(name).Read().WaitAsync(ct), ViewJson);
        }
        return kind switch
        {
            "field" => Results.Ok(await brain.Get<IField>(name).Read().WaitAsync(ct)),
            UIVocabulary.WebBrowserType => Results.Ok(await brain.Get<IWebBrowser>(name).Read().WaitAsync(ct)),
            _ => throw new ArgumentException("Unknown app UI kind.")
        };
    }

    // One UI event from the shell, under the caller the edge stamped on brain.
    // hosted answers whether the host loaded an app's code; without it no view answers a confirmation.
    // An app view's own Emit becomes AppRequested at its app; an Intent goes back to the client, which performs it.
    internal static async Task<IResult> Deliver(UiEvent input, INeuronProxies brain, string scope, IHostedAppTypes? _, CancellationToken ct, DigitalBrain.Access.Caller? caller, ILogger? logger = null)
    {
        if (input.Kind == UIVocabulary.ConfirmationType)
        {
            if (input.Name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
            var (planId, approve) = ConfirmationAnswer(input);
            ShellPresenceGate.RequireDirectPerson(caller);
            await brain.Get<IConfirmationInbox>(input.Name).Resolve(planId, approve).WaitAsync(ct);
            return Results.Ok(new { delivered = true });
        }
        if (input.Kind == UIVocabulary.ViewType) { return await DeliverView(input, brain, scope, ct, caller, logger); }
        if (!OwnsUi(scope, input.Name)) { throw new UnauthorizedAccessException(); }
        switch (input.Kind)
        {
            case UIVocabulary.WebBrowserType:
                var browser = brain.Get<IWebBrowserConnector>(input.Name);
                if (input.Action == "unavailable")
                {
                    await browser.Unavailable(input.Value ?? "The browser is unavailable.").WaitAsync(ct);
                    break;
                }
                BrowserConnect connection;
                try { connection = JsonSerializer.Deserialize<BrowserConnect>(input.Value ?? "", JsonSerializerOptions.Web) ?? throw new ArgumentException("Browser connection is required."); }
                catch (JsonException) { throw new ArgumentException("Invalid browser connection."); }
                if (input.Action == "connect") { await browser.Connect(connection.Port, connection.SessionId).WaitAsync(ct); }
                else if (input.Action == "disconnect") { await browser.Disconnect(connection.SessionId).WaitAsync(ct); }
                else { throw new ArgumentException("Unknown browser action."); }
                break;
            case "field": await brain.Get<IField>(input.Name).Input(input.Value ?? "").WaitAsync(ct); break;
            default: throw new ArgumentException("Unknown UI event.");
        }
        return Results.Ok(new { delivered = true });
    }

    // A view event: the view neuron resolves the keyed node of the generation the client saw; the edge routes what it does.
    private static async Task<IResult> DeliverView(UiEvent input, INeuronProxies brain, string scope, CancellationToken ct, DigitalBrain.Access.Caller? caller, ILogger? logger = null)
    {
        var log = logger ?? ConsoleLog;
        var key = input.Key ?? throw new ArgumentException("A view event names the activated node's key.");
        var generation = input.Generation ?? throw new ArgumentException("A view event names the generation it saw.");
        if (UnitAlias(scope, input.Name) is { } alias)
        {
            return await ActivateUnitViewAsync(brain, scope, alias, generation, key, input.Value, ct, caller, log);
        }
        if (ScreenAlias(scope, input.Name) is { } screenAlias)
        {
            PublishedView? published = null;
            IScreen? screen = null;
            try
            {
                var screenId = new NeuronId(new BrainId(scope), screenAlias, "default");
                screen = brain.Get<IScreen>(screenId);
                published = await screen.Read().WaitAsync(ct);
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
            {
                log.LogSwallowed(error, "Screen view could not be read for action finding.");
            }

            if (published is null)
            {
                var candidateUnit = screenAlias["app.".Length..];
                try
                {
                    var unitPublished = await brain.Get<IViewReader>(UnitState(scope, candidateUnit)).Read(candidateUnit).WaitAsync(ct);
                    if (unitPublished is not null)
                    {
                        return await ActivateUnitViewAsync(brain, scope, candidateUnit, generation, key, input.Value, ct, caller, log);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
                {
                    log.LogSwallowed(error, "Unit view fallback could not be read for action finding.");
                }
            }

            if (published is not null && screen is not null)
            {
                var node = ViewValidation.Find(published.View, key);
                if (node is { } found && found.TryGetProperty("resolve", out var res) && res.ValueKind == JsonValueKind.Object)
                {
                    throw new UnauthorizedAccessException($"Only the OS confirmation inbox can answer a confirmation; {input.Name} cannot.");
                }
                var action = node is { } foundNode ? ViewValidation.ActionOf(foundNode) : ViewActivation.None;

                switch (action.Kind)
                {
                    case ViewActivationKind.Intent:
                        return IntentResult(action.Intent!);

                    case ViewActivationKind.Resolve:
                        throw new UnauthorizedAccessException($"Only the OS confirmation inbox can answer a confirmation; {input.Name} cannot.");

                    case ViewActivationKind.Emit:
                        ViewArgs.RequireValueUnreserved(action.Emit!.Args, input.Value);
                        if (caller?.For?.Principal is not { Length: > 0 } personId)
                        {
                            throw new UnauthorizedAccessException("Only a signed-in person activates a screen view.");
                        }
                        try
                        {
                            await ScreenAppEmitBridge.ActivateAsync(screen, published, screenAlias, generation, key, input.Value, personId, ct: ct, logger: log).WaitAsync(ct);
                        }
                        catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.Stale)
                        {
                            throw new StaleActionException("This action is no longer available. Read the view again.");
                        }
                        catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.ForeignOwner)
                        {
                            throw new UnauthorizedAccessException($"A view of {screenAlias} cannot emit to another owner.");
                        }
                        catch (ActivationRefusedException refused)
                        {
                            throw new ArgumentException($"This view node does nothing ({refused.Reason}).");
                        }
                        return Results.Ok(new { delivered = true });

                    default:
                        throw new ArgumentException("This view node does nothing.");
                }
            }
        }
        if (!OwnsUi(scope, input.Name)) { throw new UnauthorizedAccessException(); }
        var name = input.Name;
        var activation = await brain.Get<IView>(name).Activate(generation, key, input.Value).WaitAsync(ct);
        var appKey = AppOf(scope, name);
        switch (activation.Kind)
        {
            case ViewActivationKind.Emit:
                var emit = activation.Emit!;
                if (appKey is null || !BrainId.IsValid(scope) || IntoChat.OS.Primitives.AppRef.Of(new BrainId(scope), emit.Owner).Key != appKey)
                { throw new UnauthorizedAccessException($"A view of {appKey ?? name} cannot emit to {emit.Owner}."); }
                var args = input.Value is null ? emit.Args : ViewArgs.WithValue(emit.Args, input.Value);
                await brain.Get<IApp>(appKey).Emit(emit.Signal, args?.GetRawText() ?? "").WaitAsync(ct);
                return Results.Ok(new { delivered = true });
            case ViewActivationKind.Intent:
                return IntentResult(activation.Intent!);
            case ViewActivationKind.Resolve:
                throw new UnauthorizedAccessException($"Only the OS confirmation inbox can answer a confirmation; {name} cannot.");
            default:
                throw new ArgumentException("This view node does nothing.");
        }
    }

    private static async Task<IResult> ActivateUnitViewAsync(
        INeuronProxies brain,
        string scope,
        string alias,
        long generation,
        string key,
        string? value,
        CancellationToken ct,
        DigitalBrain.Access.Caller? caller,
        ILogger? logger = null)
    {
        var log = logger ?? ConsoleLog;
        // A unit's view lives at its state neuron. IViewReader.Activate returns no typed result: it resolves the key itself and
        // journals the emit, refusing a stale generation, an unknown key, a node with no emit and an emit to another owner. The
        // edge resolves once, from the one PublishedView whose generation it checks, only to answer an intent (client-local) and
        // to refuse a resolve (a unit view never reaches the confirmations). Race: the view may change between that Read and
        // Activate; Activate re-resolves against its own current generation, so a changed view refuses the emit as stale.
        var reader = brain.Get<IViewReader>(UnitState(scope, alias));
        var published = await reader.Read(alias).WaitAsync(ct);
        IScreen? screen = null;
        if (published is null)
        {
            try
            {
                var screenId = new NeuronId(new BrainId(scope), alias, "default");
                screen = brain.Get<IScreen>(screenId);
                published = await screen.Read().WaitAsync(ct);
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
            {
                log.LogSwallowed(error, "Screen for unit could not be read.");
            }
        }
        var unitNode = published is { } shown && shown.Generation == generation ? ViewValidation.Find(shown.View, key) : null;
        if (unitNode is { } resolvedNode && resolvedNode.TryGetProperty("resolve", out var unitResolve) && unitResolve.ValueKind == JsonValueKind.Object)
        {
            throw new UnauthorizedAccessException($"A unit view cannot answer a confirmation; {alias} cannot.");
        }
        var shownAction = unitNode is { } foundUnitNode ? ViewValidation.ActionOf(foundUnitNode) : ViewActivation.None;
        switch (shownAction.Kind)
        {
            case ViewActivationKind.Intent: return IntentResult(shownAction.Intent!);
            case ViewActivationKind.Resolve: throw new UnauthorizedAccessException($"A unit view cannot answer a confirmation; {alias} cannot.");
            case ViewActivationKind.Emit: ViewArgs.RequireValueUnreserved(shownAction.Emit!.Args, value); break;
        }
        // The state neuron writes "by" into the emit, overwriting whatever the payload says: the caller's stable person id, never
        // a display name. A view that declares "by" never publishes (the framework's ViewDeclaresBy), so the edge need not refuse one.
        if (caller?.For?.Principal is not { Length: > 0 } personId)
        { throw new UnauthorizedAccessException("Only a signed-in person activates a unit view."); }
        if (screen is not null)
        {
            try { await screen.Activate(alias, generation, key, value, personId).WaitAsync(ct); }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.Stale)
            { throw new StaleActionException("This action is no longer available. Read the view again."); }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.ForeignOwner)
            { throw new UnauthorizedAccessException($"A view of {alias} cannot emit to another owner."); }
            catch (ActivationRefusedException refused)
            { throw new ArgumentException($"This view node does nothing ({refused.Reason})."); }
            return Results.Ok(new { delivered = true });
        }
        try { await reader.Activate(alias, generation, key, value, personId).WaitAsync(ct); }
        catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.Stale)
        { throw new StaleActionException("This action is no longer available. Read the view again."); }
        catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.ForeignOwner)
        { throw new UnauthorizedAccessException($"A view of {alias} cannot emit to another owner."); }
        catch (ActivationRefusedException refused)
        { throw new ArgumentException($"This view node does nothing ({refused.Reason})."); }
        return Results.Ok(new { delivered = true });
    }

    // The inbox surface's typed answer: a plan id of at most 128 characters and approve true or false; no action string.
    private static (string PlanId, bool Approve) ConfirmationAnswer(UiEvent input)
    {
        if (input.PlanId is not { Length: > 0 and <= 128 } planId || input.Approve is not { } approve || input.Action is not null)
        { throw new ArgumentException("A confirmation answer names its planId and approve."); }
        return (planId, approve);
    }

    private static IResult IntentResult(DigitalBrain.UI.Intent intent)
        => Results.Json(new { intent = new { kind = intent.Kind.ToString(), arg = intent.Arg } }, ViewJson);

    // The view's owning app, {scope}/apps/{name}.
    private static string? AppOf(string scope, string view)
    {
        if (!BrainId.IsValid(scope)) { return null; }
        var brainId = new BrainId(scope);
        if (IntoChat.OS.Primitives.AppRef.TryParse(brainId, view, out var direct)) { return direct.Key; }
        var parts = view.Split('/');
        return parts.Length > 3
            && parts[0] == scope
            && parts[1] == "apps"
            && IntoChat.OS.Primitives.AppRef.TryOf(brainId, parts[2], out var owning)
            ? owning.Key
            : null;
    }

    internal static string? ScreenAlias(string scope, string name)
    {
        if (!BrainId.IsValid(scope)) { return null; }
        var brainId = new BrainId(scope);
        var withoutView = name.EndsWith("/view", StringComparison.Ordinal) ? name[..^5] : name;
        return IntoChat.OS.Primitives.AppRef.TryParse(brainId, withoutView, out var app)
            ? "app." + app.Name
            : null;
    }

    // A unit's view is named {scope}/{alias}: one segment, a state alias rather than an app area,
    // or {scope}/units/{alias}, optionally ending in /view.
    internal static string? UnitAlias(string scope, string name) =>
        name.StartsWith(scope + "/", StringComparison.Ordinal)
        && BrainId.IsValid(scope)
        && IntoChat.OS.Primitives.UnitAlias.TryFromAddress(new BrainId(scope), name, out var alias)
            ? alias.Value
            : null;

    // The alias's state neuron, {scope}/{alias}/default.
    private static NeuronId UnitState(string scope, string alias) =>
        IntoChat.OS.Primitives.UnitAlias.TryParse(alias, out var unit)
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

    // The client's one UI event; Dart mirror: NodeContext (activate, resolve, input) in ui/ui. A confirmation answer carries PlanId
    // and Approve.
    internal sealed record UiEvent(string Kind, string Name, string? Action = null, string? Value = null, long Revision = 0, string? Field = null,
        long? Generation = null, string? Key = null, string? PlanId = null, bool? Approve = null);
    private sealed record BrowserConnect(int Port, string SessionId);
}

// An activation's field value joins the emit's args as "value" (an object's member; other args become {args, value}).
internal static class ViewArgs
{
    // The members only the edge may set: "by" names who acted (no view may declare one), and "value" is the activated field's text
    // (a view that declares one beside a field would be overwritten silently, so it is refused).
    public const string By = "by";
    public const string Value = "value";

    // A unit view's emit: "by" is the framework's (it refuses a view declaring one and stamps the host's), so only "value" is checked.
    public static void RequireValueUnreserved(JsonElement? args, string? value)
    {
        if (value is null || args is not { ValueKind: JsonValueKind.Object } given) { return; }
        if (given.EnumerateObject().Any(member => member.Value.ValueKind != JsonValueKind.Null && string.Equals(member.Name, Value, StringComparison.OrdinalIgnoreCase)))
        { throw new ArgumentException("A view's emit already declares \"value\"; a field's text cannot replace it."); }
    }

    public static void RequireUnreserved(JsonElement? args, string? value)
    {
        if (args is not { ValueKind: JsonValueKind.Object } given) { return; }
        foreach (var member in given.EnumerateObject())
        {
            if (string.Equals(member.Name, By, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A view's emit cannot declare \"by\": only the edge names who acted."); }
            if (value is not null && member.Value.ValueKind != JsonValueKind.Null && string.Equals(member.Name, Value, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A view's emit already declares \"value\"; a field's text cannot replace it."); }
        }
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
