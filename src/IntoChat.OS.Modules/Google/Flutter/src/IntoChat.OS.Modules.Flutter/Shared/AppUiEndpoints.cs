using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Confirmation;
using DigitalBrain.Errors;
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
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Flutter;

internal static class AppUiEndpoints
{
    // GET /apps/node?kind=view writes the tree with these options; the other kinds use the host's HTTP JSON options.
    internal static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);
        apps.MapPost("/connect", async ([FromServices] IEnumerable<IUiRootProvider> providers, CancellationToken ct) =>
        {
            var provider = providers.SingleOrDefault();
            return Results.Ok(new { Root = provider is null ? null : await provider.Connect(ct), provider?.ActivationApp });
        });
        apps.MapGet("/node", (string kind, string name, INeuronProxies brain, CancellationToken ct) => ReadNode(kind, name, brain, BrainScope.CurrentId(), ct));
        apps.MapPost("/event", (UiEvent input, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, CancellationToken ct)
            => Deliver(input, brain, BrainScope.CurrentId(), hosted.FirstOrDefault(), ct));

    }

    // GET /apps/node: one app UI node of the caller's brain, by kind.
    internal static async Task<IResult> ReadNode(string kind, string name, INeuronProxies brain, string scope, CancellationToken ct)
    {
        if (kind == UIVocabulary.ConfirmationType)
        {
            if (name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
            return Results.Json(ConfirmationView.Render(await brain.Get<IConfirmationInbox>(name).Read().WaitAsync(ct)), ViewJson);
        }
        var unit = kind is UIVocabulary.ViewType or "viewport" ? UnitAlias(scope, name) : null;
        if (unit is null && !OwnsUi(scope, name)) { throw new UnauthorizedAccessException("The UI belongs to another brain."); }
        if (kind == "viewport")
        {
            if (unit is not null)
            {
                var unitView = await brain.Get<IViewReader>(UnitState(scope, unit)).Read(unit).WaitAsync(ct);
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
                    if (ScreenAlias(scope, directViewName) is { } directScreenAlias)
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
                        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
                        {
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
            var appName = target.App.StartsWith(scope + "/apps/", StringComparison.Ordinal)
                ? target.App[(scope.Length + "/apps/".Length)..]
                : target.App;
            if (!appName.Contains('/'))
            {
                try
                {
                    var screenId = new NeuronId(new BrainId(scope), "app." + appName, "default");
                    var published = await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
                    if (published is not null)
                    {
                        return Results.Ok(new
                        {
                            viewport.Revision,
                            viewport.Target,
                            Root = new ViewRef(UIVocabulary.ViewType, $"{scope}/apps/{appName}/view"),
                            Error = (string?)null,
                        });
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
                {
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
                return Results.Json(await brain.Get<IViewReader>(UnitState(scope, unit)).Read(unit).WaitAsync(ct), ViewJson);
            }
            if (ScreenAlias(scope, name) is { } screenAlias)
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
                catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
                {
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
    internal static async Task<IResult> Deliver(UiEvent input, INeuronProxies brain, string scope, IHostedAppTypes? hosted, CancellationToken ct)
    {
        if (input.Kind == UIVocabulary.ConfirmationType)
        {
            if (input.Name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
            var (planId, approve) = ConfirmationAnswer(input);
            await brain.Get<IConfirmationInbox>(input.Name).Resolve(planId, approve).WaitAsync(ct);
            return Results.Ok(new { delivered = true });
        }
        if (input.Kind == UIVocabulary.ViewType) { return await DeliverView(input, brain, scope, hosted, ct); }
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
    private static async Task<IResult> DeliverView(UiEvent input, INeuronProxies brain, string scope, IHostedAppTypes? hosted, CancellationToken ct)
    {
        var key = input.Key ?? throw new ArgumentException("A view event names the activated node's key.");
        var generation = input.Generation ?? throw new ArgumentException("A view event names the generation it saw.");
        if (UnitAlias(scope, input.Name) is { } alias)
        {
            // A unit's view lives at its state neuron. IViewReader.Activate returns no typed result: it resolves the key itself and
            // journals the emit, refusing a stale generation, an unknown key, a node with no emit and an emit to another owner. The
            // edge resolves once, from the one PublishedView whose generation it checks, only to answer an intent (client-local) and
            // to refuse a resolve (a unit view never reaches the confirmations). Race: the view may change between that Read and
            // Activate; Activate re-resolves against its own current generation, so a changed view refuses the emit as stale.
            var reader = brain.Get<IViewReader>(UnitState(scope, alias));
            var published = await reader.Read(alias).WaitAsync(ct);
            var shownAction = published is { } shown && shown.Generation == generation && ViewValidation.Find(shown.View, key) is { } unitNode
                ? ViewValidation.ActionOf(unitNode) : ViewActivation.None;
            switch (shownAction.Kind)
            {
                case ViewActivationKind.Intent: return IntentResult(shownAction.Intent!);
                case ViewActivationKind.Resolve: throw new UnauthorizedAccessException($"A unit view cannot answer a confirmation; {alias} cannot.");
                case ViewActivationKind.Emit: ViewArgs.RequireValueUnreserved(shownAction.Emit!.Args, input.Value); break;
            }
            // The state neuron writes "by" into the emit, overwriting whatever the payload says: the caller's stable person id, never
            // a display name. A view that declares "by" never publishes (the framework's ViewDeclaresBy), so the edge need not refuse one.
            if (!RuntimeCallerContext.TryGet(out var caller) || string.IsNullOrEmpty(caller.PersonId))
            { throw new UnauthorizedAccessException("Only a signed-in person activates a unit view."); }
            try { await reader.Activate(alias, generation, key, input.Value, caller.PersonId).WaitAsync(ct); }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.Stale)
            { throw new StaleActionException("This action is no longer available. Read the view again."); }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.ForeignOwner)
            { throw new UnauthorizedAccessException($"A view of {alias} cannot emit to another owner."); }
            catch (ActivationRefusedException refused)
            { throw new ArgumentException($"This view node does nothing ({refused.Reason})."); }
            return Results.Ok(new { delivered = true });
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
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
            {
            }

            if (published is not null && screen is not null)
            {
                var node = ViewValidation.Find(published.View, key);
                var action = node is { } foundNode ? ViewValidation.ActionOf(foundNode) : ViewActivation.None;

                switch (action.Kind)
                {
                    case ViewActivationKind.Intent:
                        return IntentResult(action.Intent!);

                    case ViewActivationKind.Resolve:
                        var surfaceAppKey = AppOf(scope, input.Name);
                        if (hosted is null || surfaceAppKey is null || !hosted.IsHostLoaded(surfaceAppKey))
                        {
                            throw new UnauthorizedAccessException($"Only an OS surface can answer a confirmation; {input.Name} cannot.");
                        }
                        if (input.Value is not ("approve" or "deny"))
                        {
                            throw new ArgumentException("An approval is answered approve or deny.");
                        }
                        await brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(scope)).Resolve(action.Resolve!.PlanId, input.Value == "approve").WaitAsync(ct);
                        return Results.Ok(new { delivered = true });

                    case ViewActivationKind.Emit:
                        ViewArgs.RequireValueUnreserved(action.Emit!.Args, input.Value);
                        if (!RuntimeCallerContext.TryGet(out var caller) || string.IsNullOrEmpty(caller.PersonId))
                        {
                            throw new UnauthorizedAccessException("Only a signed-in person activates a screen view.");
                        }
                        try
                        {
                            await screen.Activate(screenAlias, generation, key, input.Value, caller.PersonId).WaitAsync(ct);
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
                if (appKey is null || IApp.Key(scope, emit.Owner) != appKey)
                { throw new UnauthorizedAccessException($"A view of {appKey ?? name} cannot emit to {emit.Owner}."); }
                var args = input.Value is null ? emit.Args : ViewArgs.WithValue(emit.Args, input.Value);
                await brain.Get<IApp>(appKey).Emit(emit.Signal, args?.GetRawText() ?? "").WaitAsync(ct);
                return Results.Ok(new { delivered = true });
            case ViewActivationKind.Intent:
                return IntentResult(activation.Intent!);
            case ViewActivationKind.Resolve:
                // Confirmations are an OS-surface privilege: the view's app must be host-loaded (kernel provenance, IHostedAppTypes).
                if (hosted is null || appKey is null || !hosted.IsHostLoaded(appKey))
                { throw new UnauthorizedAccessException($"Only an OS surface can answer a confirmation; {name} cannot."); }
                if (input.Value is not ("approve" or "deny")) { throw new ArgumentException("An approval is answered approve or deny."); }
                await brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(scope)).Resolve(activation.Resolve!.PlanId, input.Value == "approve").WaitAsync(ct);
                return Results.Ok(new { delivered = true });
            default:
                throw new ArgumentException("This view node does nothing.");
        }
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
        var prefix = scope + "/apps/";
        if (!view.StartsWith(prefix, StringComparison.Ordinal)) { return null; }
        var end = view.IndexOf('/', prefix.Length);
        return end > prefix.Length ? view[..end] : null;
    }

    internal static string? ScreenAlias(string scope, string name)
    {
        var prefix = scope + "/apps/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) { return null; }
        var remainder = name[prefix.Length..];
        if (remainder.EndsWith("/view", StringComparison.Ordinal))
        {
            remainder = remainder[..^5];
        }
        return remainder.Length > 0 && !remainder.Contains('/') ? "app." + remainder : null;
    }

    // A unit's view is named {scope}/{alias}: one segment, a state alias rather than an app area.
    internal static string? UnitAlias(string scope, string name)
    {
        if (!name.StartsWith(scope + "/", StringComparison.Ordinal)) { return null; }
        var alias = name[(scope.Length + 1)..];
        return alias.Length > 0 && !alias.Contains('/') && alias is not ("apps" or "images" or "applications") ? alias : null;
    }

    // The alias's state neuron, {scope}/{alias}/default.
    private static NeuronId UnitState(string scope, string alias) => new(new BrainId(scope), alias, "default");

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

    private static bool OwnsUi(string scope, string name) =>
        new[] { "/apps/", "/images/", "/applications/" }.Any(area => name.StartsWith(scope + area, StringComparison.Ordinal));

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
        if (given.EnumerateObject().Any(member => string.Equals(member.Name, Value, StringComparison.OrdinalIgnoreCase)))
        { throw new ArgumentException("A view's emit already declares \"value\"; a field's text cannot replace it."); }
    }

    public static void RequireUnreserved(JsonElement? args, string? value)
    {
        if (args is not { ValueKind: JsonValueKind.Object } given) { return; }
        foreach (var member in given.EnumerateObject())
        {
            if (string.Equals(member.Name, By, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("A view's emit cannot declare \"by\": only the edge names who acted."); }
            if (value is not null && string.Equals(member.Name, Value, StringComparison.OrdinalIgnoreCase))
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
            return JsonSerializer.SerializeToElement(items);
        }
        var payload = args is { ValueKind: JsonValueKind.Object } given ? System.Text.Json.Nodes.JsonObject.Create(given) ?? [] : new System.Text.Json.Nodes.JsonObject();
        if (args is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) } other) { payload["args"] = System.Text.Json.Nodes.JsonNode.Parse(other.GetRawText()); }
        payload["value"] = value;
        return JsonSerializer.SerializeToElement(payload);
    }
}
