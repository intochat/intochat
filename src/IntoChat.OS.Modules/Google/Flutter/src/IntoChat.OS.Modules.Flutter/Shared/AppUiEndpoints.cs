using DigitalBrain.Brains;
using IntoChat.OS.Runtime;
using DigitalBrain.Spi;
using DigitalBrain;
using DigitalBrain.Errors;
using System.Text.Json;
using DigitalBrain.Confirmation;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Table;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using DigitalBrain.Runtime.Orleans.Hosting;
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
        apps.MapGet("/node", async (string kind, string name, INeuronProxies brain, CancellationToken ct) =>
        {
            var scope = BrainScope.CurrentId();
            if (kind == UIVocabulary.ConfirmationType)
            {
                if (name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
                return Results.Json(ConfirmationView.Render(await brain.Get<IConfirmationInbox>(name).Read().WaitAsync(ct)), ViewJson);
            }
            var unit = kind == UIVocabulary.ViewType ? UnitAlias(scope, name) : null;
            if (unit is null && !OwnsUi(scope, name)) { throw new UnauthorizedAccessException("The UI belongs to another brain."); }
            if (kind == UIVocabulary.ViewType && unit is null) { name = await ResolveViewAlias(brain, scope, name, ct); }
            if (kind == "viewport")
            {
                var viewport = await brain.Get<IViewport>(name).Read().WaitAsync(ct);
                if (viewport.Target is not { } target)
                { return Results.Ok(new { viewport.Revision, Root = (UiChildRef?)null, Error = (string?)null }); }
                if (!OwnsUi(scope, target.App)) { throw new UnauthorizedAccessException(); }
                var app = await brain.Get<IApp>(target.App).Read().WaitAsync(ct);
                if (app.MigrationTarget is { } migrated)
                {
                    if (!OwnsUi(scope, migrated)) { throw new UnauthorizedAccessException(); }
                    app = await brain.Get<IApp>(migrated).Read().WaitAsync(ct);
                }
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
                return Results.Json(unit is not null
                    ? await brain.Get<IViewReader>(UnitState(scope, unit)).Read(unit).WaitAsync(ct)
                    : await brain.Get<IView>(name).Read().WaitAsync(ct), ViewJson);
            }
            return kind switch
            {
                "textfield" => Results.Ok(await brain.Get<ITextField>(name).Read().WaitAsync(ct)),
                UIVocabulary.ChatType => Results.Ok(await brain.Get<IChat>(name).Read().WaitAsync(ct)),
                UIVocabulary.ComposerType => Results.Ok(await brain.Get<IComposer>(name).Read().WaitAsync(ct)),
                UIVocabulary.WebBrowserType => Results.Ok(await brain.Get<IWebBrowser>(name).Read().WaitAsync(ct)),
                _ => throw new ArgumentException("Unknown app UI kind.")
            };
        });
        apps.MapPost("/event", (UiEvent input, INeuronProxies brain, [FromServices] IEnumerable<IHostedAppTypes> hosted, CancellationToken ct)
            => Deliver(input, brain, BrainScope.CurrentId(), hosted.FirstOrDefault(), ct));

        UiHttp.MapGet<ITable, TableState>(endpoints, "tables", neuron => neuron.Read());
        UiHttp.MapPost(endpoints, "tables/{name}/replace", async (string name, TableReplace body, [MicrosoftSdk.AspNetCore.Mvc.FromServices] IServiceProvider plain, CancellationToken ct) =>
        {
            await SiloNeurons.Get<ITable>(plain, name).Replace(body.Title, body.Columns, body.Rows).WaitAsync(ct);
            return Results.Accepted();
        });
    }

    // One UI event from the shell, under the caller the edge stamped on brain.
    // hosted answers whether the host loaded an app's code; without it no view answers a confirmation.
    // An app view's own Emit becomes AppRequested at its app; an Intent goes back to the client, which performs it.
    internal static async Task<IResult> Deliver(UiEvent input, INeuronProxies brain, string scope, IHostedAppTypes? hosted, CancellationToken ct)
    {
        if (input.Kind == UIVocabulary.ConfirmationType)
        {
            if (input.Name != IConfirmationInbox.Key(scope)) { throw new UnauthorizedAccessException(); }
            var answer = ConfirmationView.Answer(input.Action);
            await brain.Get<IConfirmationInbox>(input.Name).Resolve(answer.PlanId, answer.Approve).WaitAsync(ct);
            return Results.Ok(new { delivered = true });
        }
        if (input.Kind == UIVocabulary.ViewType) { return await DeliverView(input, brain, scope, hosted, ct); }
        if (!OwnsUi(scope, input.Name)) { throw new UnauthorizedAccessException(); }
        switch (input.Kind)
        {
            case UIVocabulary.ChatType:
                var chat = brain.Get<IChat>(input.Name);
                if (input.Read is { } read)
                {
                    if (input.Action is not null) { throw new ArgumentException("Choose a chat action or a read acknowledgement."); }
                    await chat.MarkRead(input.Context ?? "", input.Revision, read.Version, read.MessageIds).WaitAsync(ct);
                }
                else { await chat.Activate(input.Context ?? "", input.Revision, input.Action ?? "").WaitAsync(ct); }
                break;
            case UIVocabulary.ComposerType:
                var composer = brain.Get<IComposer>(input.Name);
                switch (input.Action)
                {
                    case "edit": await (input.SelectionRevision is { } editGeneration ? composer.Edit(input.Context ?? "", editGeneration, input.Value ?? "") : composer.Edit(input.Context ?? "", input.Value ?? "")).WaitAsync(ct); break;
                    case "submit": await (input.SelectionRevision is { } submitGeneration ? composer.Submit(input.OperationId ?? Guid.Empty, input.Context ?? "", submitGeneration, input.Value ?? "") : composer.Submit(input.OperationId ?? Guid.Empty, input.Context ?? "", input.Value ?? "")).WaitAsync(ct); break;
                    case "voice-begin": await (input.SelectionRevision is { } voiceGeneration ? composer.BeginVoice(input.OperationId ?? Guid.Empty, input.Context ?? "", voiceGeneration) : composer.BeginVoice(input.OperationId ?? Guid.Empty, input.Context ?? "")).WaitAsync(ct); break;
                    case "voice-audio": await composer.CaptureVoice(input.OperationId ?? Guid.Empty, DecodeAudio(input.Value), input.MimeType ?? "").WaitAsync(ct); break;
                    case "voice-cancel": await composer.CancelVoice(input.OperationId ?? Guid.Empty).WaitAsync(ct); break;
                    case "voice-error": await composer.CompleteVoice(input.OperationId ?? Guid.Empty, null, input.Value ?? "Recording failed. Try again.").WaitAsync(ct); break;
                    default: throw new ArgumentException("Unknown composer action.");
                }
                break;
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
            case "textfield": await brain.Get<ITextField>(input.Name).Input(input.Value ?? "").WaitAsync(ct); break;
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
            }
            try { await reader.Activate(alias, generation, key, input.Value).WaitAsync(ct); }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.Stale)
            { throw new StaleActionException("This action is no longer available. Read the view again."); }
            catch (ActivationRefusedException refused) when (refused.Reason == ActivationRefusedException.ForeignOwner)
            { throw new UnauthorizedAccessException($"A view of {alias} cannot emit to another owner."); }
            catch (ActivationRefusedException refused)
            { throw new ArgumentException($"This view node does nothing ({refused.Reason})."); }
            return Results.Ok(new { delivered = true });
        }
        if (!OwnsUi(scope, input.Name)) { throw new UnauthorizedAccessException(); }
        var name = await ResolveViewAlias(brain, scope, input.Name, ct);
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

    // A unit's view is named {scope}/{alias}: one segment, a state alias rather than an app area.
    internal static string? UnitAlias(string scope, string name)
    {
        if (!name.StartsWith(scope + "/", StringComparison.Ordinal)) { return null; }
        var alias = name[(scope.Length + 1)..];
        return alias.Length > 0 && !alias.Contains('/') && alias is not ("apps" or "images" or "applications") ? alias : null;
    }

    // The alias's state neuron, {scope}/{alias}/default.
    private static NeuronId UnitState(string scope, string alias) => new(new BrainId(scope), alias, "default");

    private static bool OwnsUi(string scope, string name) =>
        new[] { "/apps/", "/images/", "/applications/" }.Any(area => name.StartsWith(scope + area, StringComparison.Ordinal));

    private static byte[] DecodeAudio(string? base64)
    {
        try { return Convert.FromBase64String(base64 ?? ""); }
        catch (FormatException) { throw new ArgumentException("Audio must be base64 encoded."); }
    }

    private static async Task<string> ResolveViewAlias(INeuronProxies brain, string scope, string name, CancellationToken ct)
    {
        if (!name.EndsWith("/view", StringComparison.Ordinal)) { return name; }
        var appKey = name[..^5];
        if (IApp.NameOf(scope, appKey) is null) { return name; }
        var app = await brain.Get<IApp>(appKey).Read().WaitAsync(ct);
        if (app.MigrationTarget is not { } target) { return name; }
        if (!OwnsUi(scope, target)) { throw new UnauthorizedAccessException(); }
        return target + "/view";
    }

    internal sealed record UiEvent(string Kind, string Name, string? Action = null, string? Value = null, long Revision = 0, string? Field = null, string? MimeType = null,
        string? Context = null, Guid? OperationId = null, ChatReadInput? Read = null, long? SelectionRevision = null, long? Generation = null, string? Key = null);
    internal sealed record ChatReadInput(long Version, string[] MessageIds);
    private sealed record BrowserConnect(int Port, string SessionId);
}

// An activation's field value joins the emit's args as "value" (an object's member; other args become {args, value}).
internal static class ViewArgs
{
    // An object gets a "value" member; an array gets the value appended (a positional last argument); a scalar or no args
    // become {args?, value}.
    public static JsonElement WithValue(JsonElement? args, string value)
    {
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
