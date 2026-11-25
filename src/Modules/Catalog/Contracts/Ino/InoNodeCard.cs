using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Validates model-supplied <see cref="Node"/> trees for <see cref="IFocus.Show"/> and for rendering
/// stored <c>node</c> cards: bounded size (16 KiB) and depth (12), no <see cref="NeuronRef"/>, no
/// <see cref="WebBrowser"/>, no interactive control views or arbitrary actions (only a <see cref="Button"/>
/// whose command posts back to the thread via <c>control.submitText</c> with a 1–2,000 character
/// <c>value</c> is allowed), and non-null required collections.
/// </summary>
public static class InoNodeCard
{
    public const int MaxBytes = 16_384;
    public const int MaxDepth = 12;
    public const int MaxPostBackChars = 2_000;
    public const string PostBackSignal = "control.submitText";
    public const string PostBackTarget = ".";

    public static bool TryRead(string? json, [NotNullWhen(true)] out Node? node, [NotNullWhen(false)] out string? refusal)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            refusal = "Node JSON is empty.";
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxBytes)
        {
            refusal = $"Node JSON is {byteCount} bytes, which exceeds the {MaxBytes}-byte (16 KiB) limit.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                refusal = "Node JSON must be a JSON object with a \"kind\" property.";
                return false;
            }

            if (ValidateRawNode(doc.RootElement, depth: 1, out var rawRefusal))
            {
                refusal = rawRefusal;
                return false;
            }
        }
        catch (JsonException error)
        {
            refusal = $"Invalid JSON: {error.Message}";
            return false;
        }

        Node? parsed;
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(json);
            parsed = RuntimeProtocol.Deserialize(utf8, typeof(Node)) as Node;
        }
        catch (JsonException error)
        {
            refusal = FormatJsonError(error);
            return false;
        }
        catch (NotSupportedException error)
        {
            refusal = FormatJsonError(error);
            return false;
        }
        catch (ArgumentException error)
        {
            refusal = $"Invalid node: {error.Message}";
            return false;
        }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentException argError)
        {
            refusal = $"Invalid node: {argError.Message}";
            return false;
        }

        if (parsed is null)
        {
            refusal = "Node JSON did not deserialize to a UI node.";
            return false;
        }

        if (ValidateNode(parsed, depth: 1, out var treeRefusal))
        {
            refusal = treeRefusal;
            return false;
        }

        node = parsed;
        refusal = null;
        return true;
    }

    private static bool IsForbiddenControlViewKind(string kind) => kind is
        "checkBoxView" or
        "comboBoxView" or
        "expanderView" or
        "formView" or
        "listBoxView" or
        "queryTableView" or
        "tabStripView" or
        "toggleSwitchView" or
        "chatThreadView" or
        "liveChartView" or
        "kanbanView" or
        "stepperView" or
        "treeView" or
        "sliderView";

    private static bool ValidateRawNode(JsonElement element, int depth, [NotNullWhen(true)] out string? refusal)
    {
        if (depth > MaxDepth)
        {
            refusal = $"Node tree exceeds the maximum depth of {MaxDepth}.";
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            refusal = "Every child node must be a JSON object with a \"kind\" property.";
            return true;
        }

        if (!element.TryGetProperty("kind", out var kindProp) || kindProp.ValueKind != JsonValueKind.String)
        {
            refusal = "Node JSON must specify a string \"kind\" property.";
            return true;
        }

        var kind = kindProp.GetString()!;
        if (string.Equals(kind, "neuronRef", StringComparison.Ordinal))
        {
            refusal = "Node kind 'neuronRef' is not allowed in chat cards.";
            return true;
        }

        if (string.Equals(kind, "webBrowser", StringComparison.Ordinal))
        {
            refusal = "Node kind 'webBrowser' is not allowed in chat cards.";
            return true;
        }

        if (kind is "iconButton" or "intent")
        {
            refusal = $"Node kind '{kind}' is not allowed: interactive actions (buttons, intents, emits, commands, or submits) are forbidden in chat cards.";
            return true;
        }

        if (kind == "button" && ValidateRawButton(element, out refusal))
        {
            return true;
        }

        if (IsForbiddenControlViewKind(kind))
        {
            refusal = $"Node kind '{kind}' is an interactive control view and is not allowed: interactive actions are forbidden in chat cards.";
            return true;
        }

        if (!ViewNodeAttribute.DiscoverAll().ContainsKey(kind))
        {
            refusal = $"Unknown node kind '{kind}'. Use a supported node kind such as column, row, card, text, markdown, heading, badge, progress, dataTable, chart, or surface.";
            return true;
        }

        if (kind is "column" or "row" or "grid" or "surface" or "card"
            && element.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (ValidateRawNode(child, depth + 1, out refusal))
                {
                    return true;
                }
            }
        }

        if (kind == "card")
        {
            if (ValidateOptionalRawChild(element, "header", depth + 1, out refusal)
                || ValidateOptionalRawChild(element, "footer", depth + 1, out refusal))
            {
                return true;
            }
        }
        else if (kind == "splitPane")
        {
            if (ValidateOptionalRawChild(element, "leading", depth + 1, out refusal)
                || ValidateOptionalRawChild(element, "trailing", depth + 1, out refusal))
            {
                return true;
            }
        }
        else if (kind == "appShell")
        {
            if (ValidateOptionalRawChild(element, "sidebar", depth + 1, out refusal)
                || ValidateOptionalRawChild(element, "body", depth + 1, out refusal)
                || ValidateOptionalRawChild(element, "toolbar", depth + 1, out refusal)
                || ValidateOptionalRawChild(element, "inspector", depth + 1, out refusal))
            {
                return true;
            }
        }

        refusal = null;
        return false;
    }

    private static bool ValidateRawButton(JsonElement element, [NotNullWhen(true)] out string? refusal)
    {
        if (element.TryGetProperty("intent", out var intentProp) && intentProp.ValueKind != JsonValueKind.Null)
        {
            refusal = "Button intent actions are not allowed in chat cards: only post-back buttons targeting '.' with 'control.submitText' are permitted.";
            return true;
        }

        if (!element.TryGetProperty("command", out var commandProp) || commandProp.ValueKind != JsonValueKind.Object)
        {
            refusal = "Button action is not allowed in chat cards: only command {target:'.', signal:'control.submitText', args:{value:1..2000 chars}} is permitted.";
            return true;
        }

        var target = commandProp.TryGetProperty("target", out var targetProp) && targetProp.ValueKind == JsonValueKind.String
            ? targetProp.GetString()
            : null;
        var signal = commandProp.TryGetProperty("signal", out var signalProp) && signalProp.ValueKind == JsonValueKind.String
            ? signalProp.GetString()
            : null;
        if (!string.Equals(target, PostBackTarget, StringComparison.Ordinal)
            || !string.Equals(signal, PostBackSignal, StringComparison.Ordinal)
            || !commandProp.TryGetProperty("args", out var argsProp)
            || !IsValidPostBackArgs(argsProp))
        {
            refusal = "Button action is not allowed in chat cards: only command {target:'.', signal:'control.submitText', args:{value:1..2000 chars}} is permitted.";
            return true;
        }

        refusal = null;
        return false;
    }

    private static bool ValidateButton(Button button, [NotNullWhen(true)] out string? refusal)
    {
        if (button.Intent is not null
            || button.Command is not { } command
            || !string.Equals(command.Target, PostBackTarget, StringComparison.Ordinal)
            || !string.Equals(command.Signal, PostBackSignal, StringComparison.Ordinal)
            || command.Args is not { } args
            || !IsValidPostBackArgs(args))
        {
            refusal = "Button action is not allowed in chat cards: only command {target:'.', signal:'control.submitText', args:{value:1..2000 chars}} is permitted.";
            return true;
        }

        if (string.IsNullOrWhiteSpace(button.Key) || string.IsNullOrWhiteSpace(button.Label))
        {
            refusal = "Post-back button action requires a non-empty 'key' and 'label'.";
            return true;
        }

        refusal = null;
        return false;
    }

    private static bool IsValidPostBackArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? value = null;
        var count = 0;
        foreach (var prop in args.EnumerateObject())
        {
            count++;
            if (!string.Equals(prop.Name, "value", StringComparison.Ordinal) || prop.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            value = prop.Value.GetString();
        }

        return count == 1
            && !string.IsNullOrWhiteSpace(value)
            && value.Length is >= 1 and <= MaxPostBackChars;
    }

    private static bool ValidateOptionalRawChild(JsonElement parent, string propertyName, int depth, [NotNullWhen(true)] out string? refusal)
    {
        if (parent.TryGetProperty(propertyName, out var child) && child.ValueKind != JsonValueKind.Null)
        {
            return ValidateRawNode(child, depth, out refusal);
        }

        refusal = null;
        return false;
    }

    private static string FormatJsonError(Exception error)
    {
        var message = error.Message;
        return message.Contains("discriminator", StringComparison.OrdinalIgnoreCase)
            || message.Contains("derived", StringComparison.OrdinalIgnoreCase)
            ? $"Unknown or unsupported node kind in JSON: {message}"
            : $"Invalid JSON: {message}";
    }

    private static bool ValidateNode(Node node, int depth, [NotNullWhen(true)] out string? refusal)
    {
        if (depth > MaxDepth)
        {
            refusal = $"Node tree exceeds the maximum depth of {MaxDepth}.";
            return true;
        }

        switch (node)
        {
            case NeuronRef:
                refusal = "Node kind 'neuronRef' is not allowed in chat cards.";
                return true;

            case WebBrowser:
                refusal = "Node kind 'webBrowser' is not allowed in chat cards.";
                return true;

            case CheckBoxView or ComboBoxView or ExpanderView or FormView or ListBoxView
                or QueryTableView or TabStripView or ToggleSwitchView or ChatThreadView or LiveChartView
                or KanbanView or StepperView or DigitalBrain.UI.Controls.TreeView or SliderView:
                refusal = "Interactive control view nodes are not allowed: interactive actions are forbidden in chat cards.";
                return true;

            case IconButton or Intent:
                refusal = "Interactive actions (buttons, intents, emits, commands, or submits) are not allowed in chat cards.";
                return true;

            case Button button:
                if (ValidateButton(button, out refusal))
                {
                    return true;
                }
                break;

            case Field { OnSubmit: not null }:
                refusal = "Field submit actions (onSubmit) are not allowed in chat cards.";
                return true;

            case NavItemNode nav when nav.Command is not null || !string.IsNullOrWhiteSpace(nav.Action):
                refusal = "NavItem actions are not allowed in chat cards.";
                return true;

            case AppTileNode tile when tile.Command is not null:
                refusal = "AppTile actions are not allowed in chat cards.";
                return true;

            case InfoBar bar when bar.Close is not null || bar.Action is not null:
                refusal = "InfoBar actions (action or close) are not allowed in chat cards.";
                return true;

            case ApprovalCardNode approval:
                if (approval.Resolve is not null)
                {
                    refusal = "ApprovalCard resolve actions are not allowed in chat cards.";
                    return true;
                }
                if (approval.Rows is null || approval.Rows.Any(static r => r is null))
                {
                    refusal = "Node 'approvalCard' requires a non-null 'rows' array.";
                    return true;
                }
                break;

            case ChoiceCardNode choice:
                if (choice.Options is null || choice.Options.Any(static o => o is null))
                {
                    refusal = "Node 'choiceCard' requires a non-null 'options' array.";
                    return true;
                }
                if (choice.Options.Any(static o => o.Pick is not null))
                {
                    refusal = "ChoiceCard pick actions are not allowed in chat cards.";
                    return true;
                }
                break;

            case DraftCardNode draft when draft.Send is not null || draft.Discard is not null:
                refusal = "DraftCard actions (send or discard) are not allowed in chat cards.";
                return true;

            case RecordCardNode rec:
                if (rec.Open is not null)
                {
                    refusal = "RecordCard open actions are not allowed in chat cards.";
                    return true;
                }
                if (rec.Fields is null || rec.Fields.Any(static f => f is null))
                {
                    refusal = "Node 'recordCard' requires a non-null 'fields' array.";
                    return true;
                }
                break;

            case SecretCardNode secret when secret.Enter is not null || secret.Skip is not null:
                refusal = "SecretCard actions (enter or skip) are not allowed in chat cards.";
                return true;

            case JobCardNode job:
                if (job.Cancel is not null)
                {
                    refusal = "JobCard cancel actions are not allowed in chat cards.";
                    return true;
                }
                if (job.Steps is null || job.Steps.Any(static s => s is null))
                {
                    refusal = "Node 'jobCard' requires a non-null 'steps' array.";
                    return true;
                }
                break;

            case BrainSceneNode scene:
                if (scene.Nodes is null || scene.Synapses is null
                    || scene.Nodes.Any(static n => n is null)
                    || scene.Synapses.Any(static s => s is null))
                {
                    refusal = "Node 'brainScene' requires non-null 'nodes' and 'synapses' arrays.";
                    return true;
                }
                if (scene.Nodes.Any(static n => n.Select is not null))
                {
                    refusal = "BrainScene select actions are not allowed in chat cards.";
                    return true;
                }
                break;

            case ErrorLineNode { Action: not null }:
                refusal = "ErrorLine actions are not allowed in chat cards.";
                return true;

            case EmptyStateNode { Suggestions.Count: > 0 }:
                refusal = "EmptyState suggestion actions are not allowed in chat cards.";
                return true;

            case MetricTileNode { Actions.Count: > 0 }:
                refusal = "MetricTile actions are not allowed in chat cards.";
                return true;

            case Conversation conv:
                if (conv.Composer?.OnSubmit is not null || conv.Stop is not null)
                {
                    refusal = "Conversation actions (submit or stop) are not allowed in chat cards.";
                    return true;
                }
                if (conv.Messages is null)
                {
                    refusal = "Node 'conversation' requires a 'messages' array.";
                    return true;
                }
                if (ValidateOptionalNode(conv.Waiting, depth + 1, out refusal)
                    || ValidateOptionalNode(conv.Error, depth + 1, out refusal)
                    || ValidateOptionalNode(conv.Empty, depth + 1, out refusal)
                    || ValidateMessages(conv.Messages, depth + 1, out refusal))
                {
                    return true;
                }
                break;

            case Column col:
                return ValidateChildren(col.Children, depth + 1, "column", out refusal);

            case Row row:
                return ValidateChildren(row.Children, depth + 1, "row", out refusal);

            case Grid grid:
                return ValidateChildren(grid.Children, depth + 1, "grid", out refusal);

            case Surface surface:
                return ValidateChildren(surface.Children, depth + 1, "surface", out refusal);

            case Card card:
                if (ValidateOptionalNode(card.Header, depth + 1, out refusal)
                    || ValidateOptionalNode(card.Footer, depth + 1, out refusal)
                    || ValidateChildren(card.Children, depth + 1, "card", out refusal))
                {
                    return true;
                }
                break;

            case SplitPane split:
                if (split.Leading is null || split.Trailing is null)
                {
                    refusal = "Node 'splitPane' requires both 'leading' and 'trailing' child nodes.";
                    return true;
                }
                if (ValidateNode(split.Leading, depth + 1, out refusal)
                    || ValidateNode(split.Trailing, depth + 1, out refusal))
                {
                    return true;
                }
                break;

            case AppShellNode shell:
                if (shell.Body is null)
                {
                    refusal = "Node 'appShell' requires a 'body' child node.";
                    return true;
                }
                if (ValidateOptionalNode(shell.Sidebar, depth + 1, out refusal)
                    || ValidateNode(shell.Body, depth + 1, out refusal)
                    || ValidateOptionalNode(shell.Toolbar, depth + 1, out refusal)
                    || ValidateOptionalNode(shell.Inspector, depth + 1, out refusal))
                {
                    return true;
                }
                break;

            case DataTable table when table.Columns is null || table.Rows is null || table.Rows.Any(static r => r is null):
                refusal = "Node 'dataTable' requires non-null 'columns' and 'rows' arrays.";
                return true;

            case Chart chart when chart.Points is null || chart.Points.Any(static p => p is null):
                refusal = "Node 'chart' requires a non-null 'points' array.";
                return true;

            case Collection coll when coll.Entries is null || coll.Entries.Any(static e => e is null):
                refusal = "Node 'collection' requires a non-null 'entries' array.";
                return true;

            case DiffCardNode diff when diff.Files is null || diff.Files.Any(static f => f is null || f.Hunks is null || f.Hunks.Any(static h => h is null || h.Lines is null)):
                refusal = "Node 'diffCard' requires non-null 'files', 'hunks', and 'lines' arrays.";
                return true;
        }

        refusal = null;
        return false;
    }

    private static bool ValidateOptionalNode(Node? child, int depth, [NotNullWhen(true)] out string? refusal)
    {
        if (child is not null)
        {
            return ValidateNode(child, depth, out refusal);
        }

        refusal = null;
        return false;
    }

    private static bool ValidateChildren(IReadOnlyList<Node>? children, int depth, string kind, [NotNullWhen(true)] out string? refusal)
    {
        if (children is null)
        {
            refusal = $"Node '{kind}' requires a 'children' array.";
            return true;
        }

        foreach (var child in children)
        {
            if (child is null)
            {
                refusal = $"Node '{kind}' contains a null child.";
                return true;
            }

            if (ValidateNode(child, depth, out refusal))
            {
                return true;
            }
        }

        refusal = null;
        return false;
    }

    private static bool ValidateMessages(IReadOnlyList<MessageNode> messages, int depth, [NotNullWhen(true)] out string? refusal)
    {
        foreach (var message in messages)
        {
            if (message is null)
            {
                refusal = "Node 'conversation' contains a null message.";
                return true;
            }

            if (message.Retry is not null)
            {
                refusal = "Message retry actions are not allowed in chat cards.";
                return true;
            }

            if (ValidateOptionalNode(message.Error, depth, out refusal))
            {
                return true;
            }

            if (message.Cards is not null && ValidateChildren(message.Cards, depth, "message", out refusal))
            {
                return true;
            }
        }

        refusal = null;
        return false;
    }
}
