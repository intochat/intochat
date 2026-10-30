using System.Globalization;
using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    private static readonly HashSet<string> SupportedReferenceKinds = new(StringComparer.Ordinal)
    {
        UIVocabulary.ConfirmationType, "viewport", "chat", "composer", "text", "button", "textfield", "select",
        "fileinput", "imagecanvas", "form", "table", "chart", "voiceinput", "webbrowser",
        "toggle", "progress", "infobar", "image", "entrycontrol", "preference",
    };

    public async Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation)
    {
        RequireExecutingAssignment(expectedProgramRevision);
        ArgumentNullException.ThrowIfNull(presentation);
        if (presentation.Sphere is { } sphere) { ArgumentNullException.ThrowIfNull(sphere.BlackboardKeys); }
        var views = presentation.Views ?? [];
        if (presentation.Sphere is null && views.Length == 0)
        { throw new ArgumentException("Publish at least one named view.", nameof(presentation)); }
        if (views.Length > 16 || views.Any(view => view is null || string.IsNullOrWhiteSpace(view.Id)
            || view.Id.Length > 64 || view.Root is null) || views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Length)
        { throw new ArgumentException("A presentation has at most 16 uniquely named views with roots.", nameof(presentation)); }
        var declaredKeys = presentation.Sphere?.BlackboardKeys ?? [];
        foreach (var slot in new[] { presentation.Side, presentation.Main }.OfType<UiChildRef>().Concat(views.Select(view => view.Root)))
        {
            if (string.IsNullOrWhiteSpace(slot.Kind) || string.IsNullOrWhiteSpace(slot.Name) || slot.Name.Length > 512 || !slot.Name.StartsWith(Snapshot.BrainScopeId + "/", StringComparison.Ordinal)
                || slot.Name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            { throw new ArgumentException("A slot names a neuron of this brain.", nameof(presentation)); }
        }
        if (declaredKeys.Any(key => string.IsNullOrWhiteSpace(key) || FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal)))
        { throw new ArgumentException("Declare nonempty keys; running and attention are reserved blackboard keys.", nameof(presentation)); }
        if (presentation.Sphere?.Tree is { } tree) { FlameSchema.CheckTree(tree, declaredKeys); }
        var current = Snapshot.Presentation;
        if (current is { Root: { } root, Error: null } && JsonSerializer.Serialize(root) == JsonSerializer.Serialize(presentation))
        { return Describe(Snapshot); }
        var keys = declaredKeys.Concat(FlameSchema.ReservedBlackboardKeys).ToHashSet(StringComparer.Ordinal);
        var blackboard = (current?.Blackboard ?? []).Where(pair => keys.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        await SavePresentation(new(Snapshot.ProgramRevision, (current?.Revision ?? 0) + 1, presentation, null, blackboard));
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> FailPresentation(long expectedProgramRevision, string error)
    {
        RequireExecutingAssignment(expectedProgramRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var current = Snapshot.Presentation;
        if (current is { Root: null } && current.Error == error) { return Describe(Snapshot); }
        await SavePresentation(new(Snapshot.ProgramRevision, (current?.Revision ?? 0) + 1, null, error, new(current?.Blackboard ?? [], StringComparer.Ordinal)));
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteBlackboard(long expectedProgramRevision, Dictionary<string, string> values)
    {
        RequireExecutingAssignment(expectedProgramRevision);
        ArgumentNullException.ThrowIfNull(values);
        var current = Snapshot.Presentation ?? throw new InvalidOperationException("Publish a presentation before writing its blackboard.");
        foreach (var key in values.Keys)
        {
            if (FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal)
                || current.Root?.Sphere?.BlackboardKeys.Contains(key, StringComparer.Ordinal) != true)
            { throw new ArgumentException($"'{key}' is not a script-writable declared blackboard key.", nameof(values)); }
        }
        await WriteValues(current, values);
        return Describe(Snapshot);
    }

    public async Task Stamp(string key, string value)
    {
        if (CallerContextStamper.TryGet(out var caller) && caller.Kind != CallerKind.Platform)
        { throw new UnauthorizedAccessException("Only the module may stamp reserved blackboard keys."); }
        RequireCreated();
        if (!FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal))
        { throw new ArgumentException("Only running and attention are module-owned keys.", nameof(key)); }
        var current = Snapshot.Presentation ?? new(Snapshot.ProgramRevision, 0, null, null, new(StringComparer.Ordinal));
        await WriteValues(current, new() { [key] = value });
    }

    internal static UiNode MapView(View view, string? brainId = null)
        => Ui.Column("view", [Ui.Text("title", view.Title), MapNode("root", view.Root, brainId)]);

    internal static UiNode MapNode(string key, Node node, string? brainId = null) => node switch
    {
        Column column => Ui.Column(
            key,
            [.. column.Children.Select((child, index) => MapNode($"{key}.{index}", child, brainId))],
            Math.Max(0, column.Gap),
            column.Extents is { Count: > 0 } colExtents && colExtents.Count == column.Children.Count ? [.. colExtents] : null),

        Row row => Ui.Row(
            key,
            [.. row.Children.Select((child, index) => MapNode($"{key}.{index}", child, brainId))],
            Math.Max(0, row.Gap),
            row.Extents is { Count: > 0 } rowExtents && rowExtents.Count == row.Children.Count ? [.. rowExtents] : null),

        Grid grid => Ui.Card(
            key,
            string.Empty,
            JsonSerializer.Serialize(new { _ui = "grid", columns = Math.Max(1, grid.Columns), gap = Math.Max(0, grid.Gap) }),
            [.. grid.Children.Select((child, index) => MapNode($"{key}.{index}", child, brainId))]),

        SplitPane split => Ui.Row(
            key,
            [MapNode($"{key}.0", split.Leading, brainId), MapNode($"{key}.1", split.Trailing, brainId)],
            Math.Max(0, split.Gap),
            [Math.Max(0, split.LeadingExtent), 0]),

        Surface surface => Ui.Card(
            key,
            surface.Title ?? string.Empty,
            JsonSerializer.Serialize(new
            {
                _ui = "surface",
                kind = (surface.Kind ?? SurfaceKind.Acrylic).ToLowerInvariant(),
                subtitle = surface.Subtitle,
                gap = 12.0,
                padding = Math.Max(0, surface.Padding),
            }),
            [.. surface.Children.Select((child, index) => MapNode($"{key}.{index}", child, brainId))]),

        Tabs tabs => MapTabs(key, tabs),

        Divider divider => Ui.Divider(key, divider.Label),

        Spacer spacer => Ui.Card(
            key,
            string.Empty,
            JsonSerializer.Serialize(new { _ui = "spacer", size = Math.Max(0, spacer.Size) })),

        Collection collection => Ui.List(
            key,
            [.. collection.Entries.Select((entry, index) => Ui.Card($"{key}.entry.{index}.{entry.Id}", entry.Title, entry.Subtitle))]),

        Heading heading => new UiNode(UiNodeKind.Text, key)
        {
            Text = heading.Value,
            Detail = JsonSerializer.Serialize(new { _ui = "heading", level = heading.Level, subtitle = heading.Subtitle }),
        },

        Text text => (string.IsNullOrEmpty(text.Tone) || text.Tone == "neutral" || text.Tone == "default") && !text.Tabular && !text.Mono && text.Size is null
            ? Ui.Text(key, text.Value)
            : new UiNode(UiNodeKind.Text, key)
            {
                Text = text.Value,
                Detail = JsonSerializer.Serialize(new
                {
                    _ui = "text",
                    tone = text.Tone.ToLowerInvariant(),
                    tabular = text.Tabular,
                    mono = text.Mono,
                    size = text.Size,
                }),
            },

        Badge badge => new UiNode(UiNodeKind.Badge, key)
        {
            Text = NonEmpty(badge.Label, "•"),
            Tone = MapToneString(badge.Tone),
            Icon = SanitizeIcon(badge.Icon),
        },

        TagPill pill => new UiNode(UiNodeKind.Badge, key)
        {
            Text = NonEmpty(pill.Label, "•"),
            Tone = MapToneString(pill.Tone),
            Icon = SanitizeIcon(pill.Icon),
            Detail = JsonSerializer.Serialize(new { _ui = "tagpill", tone = pill.Tone.ToLowerInvariant() }),
        },

        Button button => new UiNode(UiNodeKind.Button, key)
        {
            Text = NonEmpty(button.Label, NonEmpty(button.Action.Operation, "Action")),
            Action = FormatAction(button.Action),
            Enabled = button.Enabled,
            Style = MapButtonStyle(button),
            Icon = SanitizeIcon(button.Icon),
            Detail = button.Small ? "small" : null,
        },

        TextField field => Ui.Card(
            key,
            field.Value ?? field.Placeholder ?? field.Name,
            JsonSerializer.Serialize(new
            {
                _ui = "textfield",
                name = field.Name,
                value = field.Value,
                placeholder = field.Placeholder,
                secret = false,
            }),
            field.OnSubmit is { } submitAction
                ? [Ui.Button($"{key}.submit", NonEmpty(field.Name, "Submit"), FormatAction(submitAction), style: UiStyle.Primary)]
                : []),

        Switch sw => Ui.Card(
            key,
            sw.Label,
            JsonSerializer.Serialize(new
            {
                _ui = "switch",
                label = sw.Label,
                value = sw.IsOn,
                enabled = sw.Action is not null,
                locked = sw.Locked,
            }),
            sw.Action is { } swAction
                ? [Ui.Button($"{key}.toggle", NonEmpty(sw.Label, "Toggle"), FormatAction(swAction), !sw.Locked)]
                : []),

        Select select => Ui.Card(
            key,
            select.Label,
            JsonSerializer.Serialize(new
            {
                _ui = "select",
                label = select.Label,
                selectedId = select.Selected,
                enabled = true,
                options = select.Options.Select(o => new { id = o.Id, label = o.Label }),
            }),
            [.. select.Options.Select((opt, index) => Ui.Button(
                $"{key}.opt.{index}",
                NonEmpty(opt.Label, opt.Id),
                FormatAction(opt.Action ?? new ButtonAction("app", "select", [opt.Id]))))]),

        Progress progress => Ui.Card(
            key,
            progress.Label,
            JsonSerializer.Serialize(new
            {
                _ui = "progress",
                value = progress.Value,
                label = progress.Label,
                detail = progress.Detail,
                indeterminate = false,
            })),

        InfoBar info => Ui.Card(
            key,
            info.Title,
            JsonSerializer.Serialize(new
            {
                _ui = "infobar",
                title = info.Title,
                body = info.Message,
                tone = info.Tone.ToLowerInvariant(),
            }),
            info.Action is { } infoBtn ? [MapNode($"{key}.action", infoBtn, brainId)] : []),

        DigitalBrain.UI.Toast toast => Ui.Toast(key, toast.Message),

        Card card => Ui.Card(
            key,
            card.Title,
            card.Body,
            [.. card.Children.Select((child, index) => MapNode($"{key}.{index}", child, brainId))]),

        DataTable table => MapDataTable(key, table),

        Chart chart => MapChart(key, chart),

        ApprovalCardNode approval => MapApprovalCard(key, approval),

        ChoiceCardNode choice => MapChoiceCard(key, choice),

        DraftCardNode draft => MapDraftCard(key, draft),

        RecordCardNode record => MapRecordCard(key, record),

        SecretCardNode secret => MapSecretCard(key, secret),

        JobCardNode job => MapJobCard(key, job),

        BrainSceneNode scene => MapBrainScene(key, scene),

        NeuronRef neuronRef => MapNeuronRef(key, neuronRef, brainId),

        AppShellNode shell => MapAppShell(key, shell, brainId),

        NavItemNode nav => MapNavItem(key, nav),

        StatCardNode stat => MapStatCard(key, stat),

        _ => throw new InvalidOperationException($"Unsupported view node {node.GetType().Name}."),
    };

    private static UiNode MapTabs(string key, Tabs tabs)
    {
        if (tabs.Items.Count == 0)
        {
            return Ui.Column(key, []);
        }

        var activeIndex = -1;
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            if (string.Equals(tabs.Items[i].Id, tabs.SelectedId, StringComparison.Ordinal))
            {
                activeIndex = i;
                break;
            }
        }
        if (activeIndex < 0) { activeIndex = 0; }

        var tabButtons = tabs.Items
            .Select((item, index) => Ui.Tab(
                $"{key}.tab.{index}",
                NonEmpty(item.Label, item.Id),
                FormatAction(item.Action),
                index == activeIndex))
            .ToArray();

        return Ui.Tabs(key, tabButtons);
    }

    private static UiNode MapDataTable(string key, DataTable table)
    {
        var buttons = new List<UiNode>();
        if (!string.IsNullOrWhiteSpace(table.SourceNeuronUri))
        {
            buttons.Add(Ui.CopyButton($"{key}.copy", "Copy URI", SanitizeCopyText(table.SourceNeuronUri!)));
        }

        return Ui.Card(
            key,
            table.Title,
            JsonSerializer.Serialize(new
            {
                _ui = "datatable",
                title = table.Title,
                columns = table.Columns,
                rows = table.Rows,
                sourceNeuronUri = table.SourceNeuronUri,
                from = table.From,
            }),
            [.. buttons]);
    }

    private static UiNode MapChart(string key, Chart chart)
    {
        var buttons = new List<UiNode>();
        if (!string.IsNullOrWhiteSpace(chart.SourceNeuronUri))
        {
            buttons.Add(Ui.CopyButton($"{key}.copy", "Copy URI", SanitizeCopyText(chart.SourceNeuronUri!)));
        }

        return Ui.Card(
            key,
            chart.Title,
            JsonSerializer.Serialize(new
            {
                _ui = "chart",
                kind = chart.Kind,
                title = chart.Title,
                subtitle = chart.From,
                unit = chart.Prefix,
                seriesName = chart.SeriesName,
                compareName = chart.CompareName,
                sourceNeuronUri = chart.SourceNeuronUri,
                points = chart.Points.Select((p, idx) => new { id = $"pt-{idx}", label = p.Label, value = p.Value }),
                comparePoints = chart.ComparePoints?.Select((v, idx) => new { id = $"cmp-{idx}", label = idx < chart.Points.Count ? chart.Points[idx].Label : $"pt-{idx}", value = v }),
            }),
            [.. buttons]);
    }

    private static UiNode MapApprovalCard(string key, ApprovalCardNode approval)
    {
        var buttons = new List<UiNode>();
        if (approval.OnApprove is { } approveAction)
        {
            buttons.Add(Ui.Button($"{key}.approve", "Approve", FormatAction(approveAction), style: UiStyle.Primary));
        }
        if (approval.OnReject is { } rejectAction)
        {
            buttons.Add(Ui.Button($"{key}.reject", "Deny", FormatAction(rejectAction), style: UiStyle.Secondary));
        }
        if (!string.IsNullOrWhiteSpace(approval.SourceNeuronUri))
        {
            buttons.Add(Ui.CopyButton($"{key}.copy", "Copy URI", SanitizeCopyText(approval.SourceNeuronUri!)));
        }

        return Ui.Card(
            key,
            approval.Title,
            JsonSerializer.Serialize(new
            {
                _ui = "approval",
                id = approval.Id,
                verb = approval.Title,
                target = string.Empty,
                rows = approval.Rows.Select(r => new { key = r.Label, value = r.NewValue, oldValue = r.OldValue, amount = r.Amount }),
                sourceApp = approval.SystemName,
                sourceNeuronUri = approval.SourceNeuronUri,
                scopeUri = approval.Summary,
                state = approval.Status,
                approveLabel = "Approve",
                rejectLabel = "Deny",
                rememberHint = approval.Summary,
            }),
            [.. buttons]);
    }

    private static UiNode MapChoiceCard(string key, ChoiceCardNode choice)
    {
        var buttons = choice.Options
            .Select((opt, index) => Ui.Button(
                $"{key}.opt.{index}",
                NonEmpty(opt.Title, $"Option {index + 1}"),
                FormatAction(opt.Action ?? new ButtonAction("app", "choose", [index.ToString(CultureInfo.InvariantCulture)]))))
            .ToArray();

        return Ui.Card(
            key,
            choice.Question,
            JsonSerializer.Serialize(new
            {
                _ui = "choice",
                question = choice.Question,
                selectedIndex = choice.PickedIndex,
                sourceApp = "Ino",
                allowOther = true,
                options = choice.Options.Select(o => new { label = o.Title, note = o.Detail }),
            }),
            buttons);
    }

    private static UiNode MapDraftCard(string key, DraftCardNode draft)
    {
        var buttons = new List<UiNode>();
        if (draft.OnSend is { } sendAction)
        {
            buttons.Add(Ui.Button($"{key}.send", "Send", FormatAction(sendAction), style: UiStyle.Primary));
        }
        if (draft.OnDiscard is { } cancelAction)
        {
            buttons.Add(Ui.Button($"{key}.cancel", "Discard", FormatAction(cancelAction), style: UiStyle.Ghost));
        }

        return Ui.Card(
            key,
            draft.Subject ?? draft.To,
            JsonSerializer.Serialize(new
            {
                _ui = "draft",
                channel = draft.Channel,
                recipient = draft.To,
                subject = draft.Subject,
                body = draft.Body,
                sent = string.Equals(draft.Status, "sent", StringComparison.OrdinalIgnoreCase),
                sendLabel = "Send",
            }),
            [.. buttons]);
    }

    private static UiNode MapRecordCard(string key, RecordCardNode record)
    {
        var buttons = new List<UiNode>();
        if (record.OnOpen is { } openAction)
        {
            buttons.Add(Ui.Button($"{key}.open", NonEmpty(record.LinkLabel, "Open"), FormatAction(openAction), style: UiStyle.Secondary));
        }

        return Ui.Card(
            key,
            record.Name,
            JsonSerializer.Serialize(new
            {
                _ui = "record",
                kind = record.ObjectLabel,
                title = record.Name,
                subtitle = record.Subtitle,
                status = "ready",
                statusTone = "accent",
                sourceApp = record.ObjectLabel,
                fields = record.Fields.Select(f => new { key = f.Label, value = f.Value }),
            }),
            [.. buttons]);
    }

    private static UiNode MapSecretCard(string key, SecretCardNode secret)
    {
        var buttons = new List<UiNode>();
        if (secret.OnEnter is { } saveAction)
        {
            buttons.Add(Ui.Button($"{key}.save", "Save to vault", FormatAction(saveAction), style: UiStyle.Primary));
        }
        if (secret.OnSkip is { } skipAction)
        {
            buttons.Add(Ui.Button($"{key}.skip", "Skip", FormatAction(skipAction), style: UiStyle.Ghost));
        }

        return Ui.Card(
            key,
            secret.Name,
            JsonSerializer.Serialize(new
            {
                _ui = "secret",
                service = secret.Name,
                keyName = secret.SavedIn,
                hint = secret.Why,
                saved = string.Equals(secret.Status, "saved", StringComparison.OrdinalIgnoreCase),
            }),
            [.. buttons]);
    }

    private static UiNode MapJobCard(string key, JobCardNode job)
    {
        var buttons = new List<UiNode>();
        if (job.OnCancel is { } cancelAction)
        {
            buttons.Add(Ui.Button($"{key}.cancel", "Cancel", FormatAction(cancelAction), style: UiStyle.Ghost));
        }

        var currentStep = 0;
        for (var i = 0; i < job.Steps.Count; i++)
        {
            if (string.Equals(job.Steps[i].Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                currentStep = i;
                break;
            }
        }

        return Ui.Card(
            key,
            job.Title,
            JsonSerializer.Serialize(new
            {
                _ui = "job",
                title = job.Title,
                currentStep,
                progress = job.Progress,
                eta = job.ResultText,
                done = string.Equals(job.Status, "done", StringComparison.OrdinalIgnoreCase),
                steps = job.Steps.Select(s => new { label = s.Label, detail = s.Detail }),
            }),
            [.. buttons]);
    }

    private static UiNode MapBrainScene(string key, BrainSceneNode scene)
    {
        var buttons = scene.Nodes
            .Select((n, index) => Ui.Button(
                $"{key}.node.{index}",
                NonEmpty(n.Label, NonEmpty(n.Id, $"Node {index + 1}")),
                FormatAction(n.OnSelect ?? new ButtonAction("app", "inspect", [n.Id]))))
            .ToArray();

        return Ui.Card(
            key,
            "Brain Scene 3D",
            JsonSerializer.Serialize(new
            {
                _ui = "brain3d",
                selectedId = scene.SelectedId,
                height = 340.0,
                interactive = true,
                showHud = true,
                nodes = scene.Nodes.Select(n => new
                {
                    id = n.Id,
                    label = n.Label,
                    category = n.Kind,
                    uri = n.Uri,
                    subtitle = n.Subtitle ?? n.Uri,
                    status = n.Status,
                    x = n.X,
                    y = n.Y,
                    z = n.Z,
                }),
                synapses = scene.Synapses.Select(s => new
                {
                    fromId = s.FromId,
                    toId = s.ToId,
                    active = s.Active,
                    signalType = s.SignalLabel ?? "Signal",
                }),
            }),
            buttons);
    }

    private static UiNode MapNeuronRef(string key, NeuronRef neuronRef, string? brainId)
    {
        var children = new List<UiNode>
        {
            Ui.CopyButton($"{key}.copy", "Copy URI", SanitizeCopyText(neuronRef.NeuronUri)),
        };

        if (!string.IsNullOrWhiteSpace(brainId)
            && SupportedReferenceKinds.Contains(neuronRef.Kind)
            && TryResolveBrainKey(neuronRef.NeuronUri, brainId!, out var resolvedKey))
        {
            children.Insert(0, Ui.Reference($"{key}.ref", new UiChildRef(neuronRef.Kind, resolvedKey)));
        }

        return Ui.Card(
            key,
            neuronRef.NeuronUri,
            JsonSerializer.Serialize(new
            {
                _ui = "neuronRef",
                kind = neuronRef.Kind,
                neuronUri = neuronRef.NeuronUri,
                title = neuronRef.NeuronUri,
            }),
            [.. children]);
    }

    private static UiNode MapAppShell(string key, AppShellNode shell, string? brainId)
    {
        var shellKey = string.IsNullOrWhiteSpace(shell.Key) ? key : shell.Key;
        var children = new List<UiNode>();
        if (shell.Sidebar is not null)
        {
            children.Add(MapNode($"{shellKey}.sidebar", shell.Sidebar, brainId));
        }
        if (shell.Toolbar is not null)
        {
            children.Add(MapNode($"{shellKey}.toolbar", shell.Toolbar, brainId));
        }
        children.Add(MapNode($"{shellKey}.body", shell.Body, brainId));
        if (shell.Inspector is not null)
        {
            children.Add(MapNode($"{shellKey}.inspector", shell.Inspector, brainId));
        }

        return Ui.Card(
            shellKey,
            string.Empty,
            JsonSerializer.Serialize(new
            {
                _ui = "appshell",
                hasSidebar = shell.Sidebar is not null,
                hasToolbar = shell.Toolbar is not null,
                hasInspector = shell.Inspector is not null,
            }),
            [.. children]);
    }

    private static UiNode MapNavItem(string key, NavItemNode nav)
    {
        var navKey = string.IsNullOrWhiteSpace(nav.Key) ? key : nav.Key;
        return Ui.Card(
            navKey,
            nav.Label,
            JsonSerializer.Serialize(new
            {
                _ui = "navitem",
                label = nav.Label,
                icon = SanitizeIcon(nav.Icon) ?? nav.Icon,
                selected = nav.Selected,
                badge = nav.Badge,
            }),
            [Ui.Button($"{navKey}.act", NonEmpty(nav.Label, "Select"), NonEmpty(nav.Action, "noop"), style: nav.Selected ? UiStyle.Primary : UiStyle.Ghost)]);
    }

    private static UiNode MapStatCard(string key, StatCardNode stat)
    {
        var statKey = string.IsNullOrWhiteSpace(stat.Key) ? key : stat.Key;
        var toneStr = stat.Tone.ToString().ToLowerInvariant();
        return Ui.Card(
            statKey,
            stat.Label,
            JsonSerializer.Serialize(new
            {
                _ui = "statcard",
                label = stat.Label,
                value = stat.Value,
                delta = stat.Delta,
                tone = toneStr,
                icon = SanitizeIcon(stat.Icon) ?? stat.Icon,
            }));
    }

    private static bool TryResolveBrainKey(string uriOrKey, string brainId, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(uriOrKey)) { return false; }
        var trimmed = uriOrKey.Trim();
        if (trimmed.StartsWith("neuron://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["neuron://".Length..];
        }
        if (trimmed.StartsWith(brainId + "/", StringComparison.Ordinal))
        {
            resolved = trimmed;
            return true;
        }
        if (!trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.StartsWith('/'))
        {
            var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                resolved = $"{brainId}/{trimmed}";
                return true;
            }
        }
        return false;
    }

    private static string FormatAction(ButtonAction action)
    {
        if (string.Equals(action.Operation, "copy", StringComparison.OrdinalIgnoreCase) && action.Arguments.Count > 0)
        {
            return "copy:" + SanitizeCopyText(action.Arguments[0]);
        }

        var op = string.IsNullOrWhiteSpace(action.Operation) ? "noop" : action.Operation;
        return action.Arguments.Count > 0
            ? op + ":" + string.Join(':', action.Arguments.Select(Uri.EscapeDataString))
            : op;
    }

    private static UiStyle MapButtonStyle(Button button)
    {
        var style = (button.Style ?? string.Empty).Trim().ToLowerInvariant();
        var tone = (button.Tone ?? string.Empty).Trim().ToLowerInvariant();
        if (tone == "danger" || style == "danger")
        {
            return UiStyle.Danger;
        }
        if (style == "primary" || tone == ButtonTone.Accent)
        {
            return UiStyle.Primary;
        }
        return style switch
        {
            "ghost" => UiStyle.Ghost,
            "link" => UiStyle.Link,
            _ => UiStyle.Secondary,
        };
    }

    private static UiTone MapToneString(string? tone) => (tone ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "done" or "ok" or "success" => UiTone.Done,
        "working" or "warn" or "warning" or "accent" or "info" => UiTone.Working,
        "error" or "danger" => UiTone.Error,
        _ => UiTone.Neutral,
    };

    private static string NonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SanitizeCopyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return "neuron://"; }
        var chars = value.Where(c => c >= '\u0020' && (c < '\u007f' || c > '\u009f')).Take(2000).ToArray();
        return chars.Length == 0 ? "neuron://" : new string(chars);
    }

    private static string? SanitizeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) { return null; }
        var normalized = new string(icon
            .Trim()
            .ToLowerInvariant()
            .Select(c => c == '_' || c == ' ' ? '-' : c)
            .Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            .Take(64)
            .ToArray())
            .Trim('-');
        return normalized.Length == 0 ? null : normalized;
    }

    private async Task WriteValues(AppPresentation current, Dictionary<string, string> values)
    {
        if (values.All(pair => current.Blackboard.TryGetValue(pair.Key, out var held) && held == pair.Value)) { return; }
        var blackboard = new Dictionary<string, string>(current.Blackboard, StringComparer.Ordinal);
        foreach (var (key, value) in values) { blackboard[key] = value; }
        await SavePresentation(current with { Revision = current.Revision + 1, Blackboard = blackboard });
    }

    private Task SavePresentation(AppPresentation presentation)
    {
        var next = Snapshot.Clone();
        next.Presentation = presentation;
        return Save(next, new PresentationChanged(Key, next.ProgramRevision, presentation.Revision));
    }

    private void RequireExecutingAssignment(long expectedProgramRevision)
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.Kind != CallerKind.App
            || !string.Equals(caller.AppId, Key, StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("Only this app's executing assignment may publish its presentation."); }
        RequireCreated();
        if (expectedProgramRevision != Snapshot.ProgramRevision)
        { throw new InvalidOperationException("The program was replaced; this assignment may not publish."); }
    }
}
