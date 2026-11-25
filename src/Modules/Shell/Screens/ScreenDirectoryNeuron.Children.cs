using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.State;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Flutter.Screens;

internal sealed partial class ScreenDirectoryNeuron
{
    private const int MaxChildDepth = 4;

    // A screen's children drawn in place for MCP and HTTP readers: each child ref is replaced by the child's view root, whose node keys
    // carry the child key path ("conversation/composer"); Act routes such a key to the child. A child that cannot be read keeps its ref.
    private async Task<JsonElement> InlineChildrenAsync(JsonElement view)
    {
        if (!view.GetRawText().Contains("\"" + NeuronRef.ChildKind + "\"", StringComparison.Ordinal)) { return view; }
        var root = System.Text.Json.Nodes.JsonNode.Parse(view.GetRawText());
        if (root is null) { return view; }
        await InlineAsync(root, "", 0);
        return JsonSerializer.SerializeToElement(root);
    }

    private async Task InlineAsync(System.Text.Json.Nodes.JsonNode node, string prefix, int depth)
    {
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.ToArray()) { if (item is not null) { await InlineAsync(item, prefix, depth); } }
            return;
        }
        if (node is not System.Text.Json.Nodes.JsonObject obj) { return; }
        if (ChildOf(obj) is { } child)
        {
            try
            {
                if (await ResolveNeuron<IScreen>(child.Id).Read() is { } childView && childView.View.TryGetProperty("root", out var childRoot))
                {
                    // Keys first, then the child's own children, so a nested child's keys carry its whole path once.
                    var path = prefix + child.Key + "/";
                    var inlined = System.Text.Json.Nodes.JsonNode.Parse(childRoot.GetRawText())!.AsObject();
                    PrefixKeys(inlined, path);
                    if (depth + 1 < MaxChildDepth) { await InlineAsync(inlined, path, depth + 1); }
                    // The parent's props for the child (status, placeholder, an error) stay readable on the node drawn in its place.
                    if (obj["props"] is { } childProps) { inlined["props"] = childProps.DeepClone(); }
                    obj.Clear();
                    foreach (var (name, value) in inlined.ToArray()) { inlined.Remove(name); obj[name] = value; }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "Child read failed.");
            }
            return;
        }
        foreach (var (name, value) in obj.ToArray())
        {
            if (value is not null && name is not ("command" or "args" or "intent" or "resolve" or "onSubmit" or "props")) { await InlineAsync(value, prefix, depth); }
        }
    }

    // The node keys of an inlined child view, prefixed with its child path; action payloads and props are data, not nodes.
    private static void PrefixKeys(System.Text.Json.Nodes.JsonNode node, string path)
    {
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array) { if (item is not null) { PrefixKeys(item, path); } }
            return;
        }
        if (node is not System.Text.Json.Nodes.JsonObject obj) { return; }
        if (obj["key"] is System.Text.Json.Nodes.JsonValue keyValue && keyValue.TryGetValue<string>(out var key)) { obj["key"] = path + key; }
        foreach (var (name, value) in obj.ToArray())
        {
            if (value is not null && name is not ("command" or "args" or "intent" or "resolve" or "onSubmit" or "props")) { PrefixKeys(value, path); }
        }
    }

    private static (string Key, NeuronId Id)? ChildOf(System.Text.Json.Nodes.JsonObject obj)
        => obj["kind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["kind"] == "neuronRef"
            && obj["refKind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["refKind"] == NeuronRef.ChildKind
            && obj["neuronUri"]?.GetValueKind() == JsonValueKind.String && NeuronId.TryParse((string?)obj["neuronUri"], out var id)
            && id.Key.LastIndexOf('/') is var slash and > 0
                ? (id.Key[(slash + 1)..], id)
                : null;

    // The screen that takes the app's commands and its current view: the one the resolver names, or else (no resolver) the first
    // host that has a view, in the order Read tries them.
    private async Task<(NeuronId Id, PublishedView View)?> TopScreenAsync(string brainId, string appId, string normalizedApp, string kind)
    {
        foreach (var id in await ScreenCandidatesAsync(brainId, appId, normalizedApp, kind))
        {
            try
            {
                if (await ResolveNeuron<IScreen>(id).Read() is { } view) { return (id, view); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, $"Could not read screen '{id}' to act on it.");
            }
        }
        return null;
    }

    private async Task<NeuronId[]> ScreenCandidatesAsync(string brainId, string appId, string normalizedApp, string kind)
    {
        if (!BrainId.IsValid(brainId)) { return []; }
        var brain = new BrainId(brainId);
        if (targetResolver is not null)
        {
            return await targetResolver.ResolveAsync(brain, normalizedApp) is { Kind: not IScreenTargetResolver.ConfirmationsKind } resolved ? [resolved.ScreenId] : [];
        }
        var unit = new NeuronId(brain, normalizedApp, "default");
        if (string.Equals(kind, IScreenTargetResolver.AgentAppKind, StringComparison.Ordinal)) { return [new NeuronId(brain, "agentapp", normalizedApp)]; }
        if (string.Equals(kind, IScreenTargetResolver.UnitKind, StringComparison.Ordinal)) { return [unit]; }
        var viewName = appId.Contains('/', StringComparison.Ordinal) ? appId : IntoChat.OS.Host.Primitives.AppRef.Of(brain, normalizedApp).ViewKey;
        var view = PlainNeuronContracts.IdOf(typeof(IView), viewName);
        return !normalizedApp.Contains('.', StringComparison.Ordinal) || normalizedApp.StartsWith("app.", StringComparison.Ordinal)
            ? [new NeuronId(brain, normalizedApp.StartsWith("app.", StringComparison.Ordinal) ? normalizedApp : "app." + normalizedApp, "default"), view, unit]
            : [view, unit];
    }

    // The view a key path's node is in: while the leading segment names a child of the view, the path descends into that child, read
    // through the top-level screen as a client reads it. Returns the child path ("" for the screen itself), its view and the node key.
    private async Task<(string Path, PublishedView View, string Key)> NodeViewAsync(NeuronId screen, PublishedView top, string key)
    {
        var path = "";
        var view = top;
        var rest = key;
        for (var depth = 0; depth < MaxChildDepth && rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0; depth++)
        {
            var segment = rest[..slash];
            if (FindChild(view.View, segment) is null) { break; }
            path = path.Length == 0 ? segment : path + "/" + segment;
            view = await ResolveNeuron<IScreen>(screen).ReadChild(path) ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownChild);
            rest = rest[(slash + 1)..];
        }
        return (path, view, rest);
    }

    // A node's command target, relative to the view it is in, as a target of the top-level screen: the child path, then the target.
    private static string Through(string path, Command command)
    {
        if (path.Length == 0) { return command.Target; }
        if (ViewValidation.ToSelf(command)) { return path; }
        return path + "/" + (command.Target.StartsWith("./", StringComparison.Ordinal) ? command.Target[2..] : command.Target);
    }

    // A node that only names a client-local intent and sends no command.
    private static bool IsIntent(JsonElement node)
        => !HasObject(node, "command") && !HasObject(node, "onSubmit") && !HasObject(node, "close")
            && (HasObject(node, "intent") || node.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "intent");

    private static bool HasObject(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;

    private static NeuronId? FindChild(JsonElement view, string childKey)
    {
        var pending = new Stack<JsonElement>([view]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("refKind", out var refKind) && refKind.ValueKind == JsonValueKind.String && refKind.GetString() == NeuronRef.ChildKind
                    && element.TryGetProperty("neuronUri", out var uri) && uri.ValueKind == JsonValueKind.String
                    && NeuronId.TryParse(uri.GetString(), out var child) && child.Key.EndsWith("/" + childKey, StringComparison.Ordinal))
                {
                    return child;
                }
                foreach (var property in element.EnumerateObject()) { pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { pending.Push(item); }
            }
        }
        return null;
    }

    private static ScreenNodeItem[] ExtractNodes(JsonElement? root)
    {
        if (root is null || root.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        var results = new List<ScreenNodeItem>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonElement>([root.Value]);

        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                var key = element.TryGetProperty("key", out var kEl) && kEl.ValueKind == JsonValueKind.String
                    ? kEl.GetString()
                    : (element.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null);

                var kind = element.TryGetProperty("kind", out var kdEl) && kdEl.ValueKind == JsonValueKind.String
                    ? kdEl.GetString()!
                    : (element.TryGetProperty("fieldKind", out _) ? "field" : "action");

                var label = element.TryGetProperty("label", out var lEl) && lEl.ValueKind == JsonValueKind.String
                    ? lEl.GetString()!
                    : (element.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString()!
                        : (element.TryGetProperty("text", out var txEl) && txEl.ValueKind == JsonValueKind.String
                            ? txEl.GetString()!
                            : key ?? ""));

                var value = element.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.String
                    ? vEl.GetString()
                    : null;

                var isInteractive = key is not null && (
                    kind is "button" or "field" or "tabItem" or "tab" or "action" or "approvalCard" or "confirmCard" or "navItem" or "appTile"
                    || element.TryGetProperty("intent", out _)
                    || element.TryGetProperty("resolve", out _)
                    || element.TryGetProperty("onSubmit", out _)
                    || element.TryGetProperty("fieldKind", out _));

                if (key is not null && isInteractive && seenKeys.Add(key))
                {
                    results.Add(new ScreenNodeItem(key, kind, label, value));
                }

                foreach (var prop in element.EnumerateObject().Where(p => p.Name is not ("command" or "args" or "intent" or "resolve")))
                {
                    pending.Push(prop.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    pending.Push(item);
                }
            }
        }

        return [.. results];
    }

    private async Task<ScreenApprovalItem[]> ExtractApprovals(string brain, string appId, JsonElement? root)
    {
        var items = new List<ScreenApprovalItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // 1. From IApprovals / IConfirmationInbox
        try
        {
            var approvalsNeuron = ResolveNeuron<IApprovals>(IApprovals.Key(brain));
            var pending = await approvalsNeuron.Pending();
            foreach (var a in pending)
            {
                if (appId == "confirmations" || string.Equals(a.Subject, appId, StringComparison.OrdinalIgnoreCase) || a.Detail.Contains(appId, StringComparison.OrdinalIgnoreCase))
                {
                    if (seenIds.Add(a.PlanId) && !CurrentState.ResolvedApprovals.Contains(a.PlanId, StringComparer.Ordinal))
                    {
                        items.Add(new ScreenApprovalItem(a.PlanId, a.Title, a.Detail, appId, "pending"));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, "IApprovals could not be read for approval extraction.");
            try
            {
                var inbox = await ResolveNeuron<IConfirmationInbox>(IConfirmationInbox.Key(brain)).Read();
                foreach (var req in inbox.Pending)
                {
                    if (seenIds.Add(req.PlanId) && !CurrentState.ResolvedApprovals.Contains(req.PlanId, StringComparer.Ordinal))
                    {
                        items.Add(new ScreenApprovalItem(req.PlanId, req.Plan.Title, req.Plan.Body, appId, "pending"));
                    }
                }
            }
            catch (Exception inboxEx) when (inboxEx is not OperationCanceledException)
            {
                Log.LogSwallowed(inboxEx, "IConfirmationInbox could not be read for approval extraction.");
            }
        }

        // 2. From UI tree nodes that declare resolve
        if (root is not null && root.Value.ValueKind == JsonValueKind.Object)
        {
            var pending = new Stack<JsonElement>([root.Value]);
            while (pending.Count > 0)
            {
                var el = pending.Pop();
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("resolve", out var res) && res.ValueKind == JsonValueKind.Object
                        && res.TryGetProperty("planId", out var pid) && pid.ValueKind == JsonValueKind.String)
                    {
                        var planId = pid.GetString()!;
                        if (seenIds.Add(planId) && !CurrentState.ResolvedApprovals.Contains(planId, StringComparer.Ordinal))
                        {
                            var title = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "Approval Request";
                            var summary = el.TryGetProperty("subtitle", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
                            items.Add(new ScreenApprovalItem(planId, title, summary, appId, "pending"));
                        }
                    }
                    foreach (var prop in el.EnumerateObject().Where(p => p.Name is not ("command" or "args")))
                    {
                        pending.Push(prop.Value);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                    {
                        pending.Push(item);
                    }
                }
            }
        }

        return [.. items];
    }

    private static string NormalizeAppId(string brain, string app)
    {
        if (BrainId.IsValid(brain) && IntoChat.OS.Host.Primitives.AppRef.TryParse(new BrainId(brain), app, out var appRef))
        {
            return appRef.Name;
        }
        var prefix = brain + "/units/";
        if (app.StartsWith(prefix, StringComparison.Ordinal))
        {
            return app[prefix.Length..];
        }
        prefix = brain + "/";
        if (app.StartsWith(prefix, StringComparison.Ordinal))
        {
            return app[prefix.Length..];
        }
        if (app.StartsWith("apps/", StringComparison.Ordinal))
        {
            return app[5..];
        }
        if (app.StartsWith("app.", StringComparison.Ordinal))
        {
            return app[4..];
        }
        return app;
    }

    private static string FormatTitle(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) { return ""; }
        return char.ToUpperInvariant(appId[0]) + appId[1..];
    }
}

[GenerateSerializer, Alias("ui.screen-directory-state")]
[ExportableState]
internal sealed record ScreenDirectoryState
{
    [Id(0)] public string[] ResolvedApprovals { get; init; } = [];
}

[Signal("ui.screens.approval-resolved")]
internal sealed record ApprovalResolved(string BrainId, string ApprovalId, bool Approved) : DigitalBrain.Signal;
