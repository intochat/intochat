using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Log;
using DigitalBrain.State;
using IntoChat.OS.Modules.Apps.Approvals;

namespace IntoChat.OS.Modules.Flutter.Screens;

internal sealed partial class ScreenDirectoryNeuron
{
    private const int MaxSlotDepth = 4;

    // A screen's slots drawn in place for MCP and HTTP readers: each slot is replaced by its child's view root, whose node keys carry
    // the slot key path ("conversation/composer"); Act routes such a key to the child. A child that cannot be read keeps its slot.
    private async Task<JsonElement> InlineSlotsAsync(JsonElement view)
    {
        if (!view.GetRawText().Contains("\"slot\"", StringComparison.Ordinal)) { return view; }
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
        if (SlotOf(obj) is { } slot)
        {
            try
            {
                if (await ResolveNeuron<IScreen>(slot.Child).Read() is { } child && child.View.TryGetProperty("root", out var childRoot))
                {
                    // Keys first, then the child's own slots, so a nested slot's keys carry its whole path once.
                    var path = prefix + slot.Key + "/";
                    var inlined = System.Text.Json.Nodes.JsonNode.Parse(childRoot.GetRawText())!.AsObject();
                    PrefixKeys(inlined, path);
                    if (depth + 1 < MaxSlotDepth) { await InlineAsync(inlined, path, depth + 1); }
                    // The parent's props for the slot (status, placeholder, an error) stay readable on the node drawn in its place.
                    if (obj["props"] is { } slotProps) { inlined["props"] = slotProps.DeepClone(); }
                    obj.Clear();
                    foreach (var (name, value) in inlined.ToArray()) { inlined.Remove(name); obj[name] = value; }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "Slot read failed.");
            }
            return;
        }
        foreach (var (name, value) in obj.ToArray())
        {
            if (value is not null && name is not ("command" or "emit" or "args" or "intent" or "resolve" or "onSubmit" or "props")) { await InlineAsync(value, prefix, depth); }
        }
    }

    // The node keys of an inlined child view, prefixed with its slot path; action payloads and props are data, not nodes.
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
            if (value is not null && name is not ("command" or "emit" or "args" or "intent" or "resolve" or "onSubmit" or "props")) { PrefixKeys(value, path); }
        }
    }

    private static (string Key, NeuronId Child)? SlotOf(System.Text.Json.Nodes.JsonObject obj)
        => obj["kind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["kind"] == "neuronRef"
            && obj["refKind"]?.GetValueKind() == JsonValueKind.String && (string?)obj["refKind"] == "slot"
            && obj["neuronUri"]?.GetValueKind() == JsonValueKind.String && NeuronId.TryParse((string?)obj["neuronUri"], out var child)
            && child.Key.LastIndexOf('/') is var slash and > 0
                ? (child.Key[(slash + 1)..], child)
                : null;

    // The slot child and its node key a key path names: "slot/key" while the leading segment names a slot of the view (descending
    // through nested slots); null when the key names no slot.
    private async Task<(NeuronId Child, string Key)?> SlotTargetAsync(JsonElement view, string key)
    {
        (NeuronId Child, string Key)? target = null;
        var current = view;
        var rest = key;
        for (var depth = 0; depth < MaxSlotDepth && rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0; depth++)
        {
            var segment = rest[..slash];
            if (FindSlot(current, segment) is not { } child) { break; }
            target = (child, rest[(slash + 1)..]);
            rest = rest[(slash + 1)..];
            if (await ResolveNeuron<IScreen>(child).Read() is not { } next) { break; }
            current = next.View;
        }
        return target;
    }

    private static NeuronId? FindSlot(JsonElement view, string slotKey)
    {
        var pending = new Stack<JsonElement>([view]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("refKind", out var refKind) && refKind.ValueKind == JsonValueKind.String && refKind.GetString() == "slot"
                    && element.TryGetProperty("neuronUri", out var uri) && uri.ValueKind == JsonValueKind.String
                    && NeuronId.TryParse(uri.GetString(), out var child) && child.Key.EndsWith("/" + slotKey, StringComparison.Ordinal))
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

    private static ScreenControlItem[] ExtractControls(JsonElement? root)
    {
        if (root is null || root.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        var results = new List<ScreenControlItem>();
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
                    || element.TryGetProperty("emit", out _)
                    || element.TryGetProperty("intent", out _)
                    || element.TryGetProperty("resolve", out _)
                    || element.TryGetProperty("onSubmit", out _)
                    || element.TryGetProperty("fieldKind", out _));

                if (key is not null && isInteractive && seenKeys.Add(key))
                {
                    results.Add(new ScreenControlItem(key, kind, label, value));
                }

                foreach (var prop in element.EnumerateObject().Where(p => p.Name is not ("emit" or "args" or "intent" or "resolve")))
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
                    foreach (var prop in el.EnumerateObject().Where(p => p.Name is not ("emit" or "args")))
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
