using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Gallery;

internal static class GalleryRendering
{
    public static View RenderSide(GalleryAppState state)
    {
        var visible = GalleryLibrary.FilteredEntries(state.Entries, state.Category, state.Search);
        var categoryButtons = new (string Id, string Title, string Icon, string Emit, bool Selected, int Count)[]
        {
            ("cat-home", "Catalog Grid (Home)", "house", "select:home", state.ViewMode == "grid", state.Entries.Length),
            ("cat-all", "All Controls", "layout-grid", "filter:all", state.Category == "all" && state.ViewMode != "grid", state.Entries.Length),
            ("cat-design", "Design & Identity", "palette", "filter:design", state.Category == "design", state.Entries.Count(e => e.Category == "design")),
            ("cat-controls", "Basic Input", "mouse-pointer-click", "filter:controls", state.Category == "controls", state.Entries.Count(e => e.Category == "controls")),
            ("cat-layout", "Layout & App Shell", "panel-left", "filter:layout", state.Category == "layout", state.Entries.Count(e => e.Category == "layout")),
            ("cat-lfp", "Data & 13 LFP Chat Cards", "table", "filter:chat-lfp", state.Category == "chat-lfp", state.Entries.Count(e => e.Category == "chat-lfp")),
            ("cat-3d", "3D Brain & Neurons", "brain-circuit", "filter:3d-brain", state.Category == "3d-brain", state.Entries.Count(e => e.Category == "3d-brain")),
        }.Select(c => (Node)Ui.NavItem(c.Id, c.Title, c.Icon, c.Emit, selected: c.Selected, badge: $"{c.Count}")).ToArray();

        var entryNavItems = visible
            .Select(entry => (Node)Ui.NavItem(
                $"sample-{entry.Id}",
                entry.Title,
                entry.Icon,
                $"select:{entry.Id}",
                selected: state.ViewMode != "grid" && string.Equals(state.SelectedId, entry.Id, StringComparison.OrdinalIgnoreCase),
                badge: GalleryLibrary.CategoryShortBadge(entry.Category)))
            .ToArray();

        return new(
            "UI Kit Catalog",
            new Column(
            [
                new Surface(
                    [
                        new Row([new TagPill(state.StylePreset, "accent", "palette"), new TagPill(state.BarLayout, "neutral", "layout")]) { Gap = 6 },
                        new Text($"Identity: {state.StylePreset} · {state.BarLayout}") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "UI Gallery · Component Studio"),
                new Field("gallery-search", "gallery-search", FieldKind.Search, "Search 5 categories & LFP cards…", AppSignals.Emit("gallery", "filter", state.Category, state.Search)),
                new Divider("CATEGORIES (5)"),
                new Column(categoryButtons) { Gap = 4 },
                new Divider($"CONTROLS ({visible.Length})"),
                new Column(entryNavItems) { Gap = 4 },
            ])
            { Gap = 10 });
    }

    public static View Render(GalleryAppState state)
    {
        var selected = state.Entries.FirstOrDefault(e => e.Id == state.SelectedId) ?? state.Entries[0];
        var visible = GalleryLibrary.FilteredEntries(state.Entries, state.Category, state.Search);

        if (string.Equals(state.ViewMode, "grid", StringComparison.OrdinalIgnoreCase))
        {
            return new("UI Gallery", new Column([RenderStudioHeroBanner(state, selected), RenderCatalogGridSection(state, visible)]) { Gap = 16 });
        }

        var stageSurfaceKind = state.KnobSurface switch
        {
            "recessed" => SurfaceKind.Recessed,
            "acrylic" => SurfaceKind.Acrylic,
            _ => SurfaceKind.Elevated,
        };

        return new(
            "UI Gallery",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new Button("back-to-gallery", "Back to Gallery", Emit: AppSignals.Emit("gallery", "select", "home"), Tone: ButtonTone.Neutral) { Icon = "arrow-left", Small = true },
                            new TagPill(GalleryLibrary.CategoryDisplayName(selected.Category), "accent", selected.Icon),
                            new TagPill(selected.NeuronContract, "ok", "cpu"),
                            new TagPill($"[Signal] {selected.Signals.FirstOrDefault() ?? "gallery.select"} ({state.SignalPulseCount})", "done", "zap"),
                            new TagPill($"{state.StylePreset} · {state.BarLayout}", "neutral", "palette"),
                        ]) { Gap = 8 },
                        new Heading(selected.Title, Level: 1, Subtitle: selected.Summary),
                        new Text(state.Status) { Tone = "muted", Tabular = true },
                        new Text($"Control: {selected.Title} [{selected.NeuronContract}]") { Tabular = true },
                        new Text($"URI: {selected.SourceNeuronUri}") { Mono = true, Tabular = true },
                        new Text($"Signals: {string.Join(", ", selected.Signals)}") { Mono = true },
                        new NeuronRef("gallery", selected.SourceNeuronUri),
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "UI Gallery · Interactive Control Playground"),
                new SplitPane(
                    new Surface(
                        [BuildSamplePreviewNode(state, selected)],
                        Kind: stageSurfaceKind,
                        Title: "Live Control Preview",
                        Subtitle: $"{selected.NeuronContract} · State: {state.KnobState} · Surface: {state.KnobSurface}"),
                    RenderPropertyKnobsSurface(state),
                    LeadingExtent: 540,
                    Gap: 14),
                RenderCodeAndIdentityInspector(state, selected),
            ])
            { Gap = 16 });
    }

    private static Node RenderStudioHeroBanner(GalleryAppState state, GalleryCatalogEntry selected) => new Surface(
        [
            new Heading("IntoChat UI Gallery · Single-Format Component Studio", Level: 1, Subtitle: "7 UI Identities · Squircle IcAppIcon · Ergonomic C# DigitalBrain.UI Builders · 13 Interactive LFP Chat Cards · 3D Neural Graph"),
            new Row(
            [
                new TagPill($"{state.Entries.Length} Components", "accent", "sparkles"),
                new TagPill("13 LFP Chat Cards", "done", "table"),
                new TagPill($"{state.StylePreset} · {state.BarLayout}", "neutral", "palette"),
                new Button("inspect-selected-control", "Inspect Selected Control", Emit: AppSignals.Emit("gallery", "select", selected.Id), Tone: ButtonTone.Accent) { Icon = "arrow-up-right", Small = true },
            ]) { Gap = 8 },
            RenderIdentityPresetRow(state),
            new Text(state.Status) { Tone = "muted", Tabular = true },
            new Text($"Control: {selected.Title} [{selected.NeuronContract}] · {selected.SourceNeuronUri}") { Mono = true, Tabular = true, Tone = "subtle" },
        ],
        Kind: SurfaceKind.Acrylic,
        Title: "UI Gallery · WinUI 3 Component Studio");

    private static Node RenderPropertyKnobsSurface(GalleryAppState state) => new Surface(
        [
            new Text("STYLE (Primary · Secondary · Ghost · Danger)") { Tone = "muted", Size = 11 },
            new Row(new[] { "primary", "secondary", "ghost", "danger" }.Select(s => (Node)KnobButton(char.ToUpperInvariant(s[0]) + s[1..], $"knob:style:{s}", state.KnobStyle == s)).ToArray()) { Gap = 6 },
            new Text("TONE (Neutral · Accent · Done · Working · Error)") { Tone = "muted", Size = 11 },
            new Row(new[] { "neutral", "accent", "done", "working", "error" }.Select(t => (Node)KnobButton(char.ToUpperInvariant(t[0]) + t[1..], $"knob:tone:{t}", state.KnobTone == t)).ToArray()) { Gap = 6 },
            new Text("SIZE (Standard 36px · Small 30px)") { Tone = "muted", Size = 11 },
            new Row([KnobButton("Standard (36px)", "knob:size:standard", !state.KnobSmall), KnobButton("Small (30px)", "knob:size:small", state.KnobSmall)]) { Gap = 6 },
            new Text("STATE (Interactive · Loading · Disabled · Empty)") { Tone = "muted", Size = 11 },
            new Row(new[] { "interactive", "loading", "disabled", "empty" }.Select(st => (Node)KnobButton(char.ToUpperInvariant(st[0]) + st[1..], $"knob:state:{st}", state.KnobState == st)).ToArray()) { Gap = 6 },
            new Text("SURFACE (Acrylic · Elevated · Recessed)") { Tone = "muted", Size = 11 },
            new Row(new[] { "acrylic", "elevated", "recessed" }.Select(sf => (Node)KnobButton(char.ToUpperInvariant(sf[0]) + sf[1..], $"knob:surface:{sf}", state.KnobSurface == sf)).ToArray()) { Gap = 6 },
            new Text("CHART KIND (Bar · Line)") { Tone = "muted", Size = 11 },
            new Row([KnobButton("Bar", "knob:chart:bar", state.KnobChartKind == "bar"), KnobButton("Line", "knob:chart:line", state.KnobChartKind == "line")]) { Gap = 6 },
        ],
        Kind: SurfaceKind.Recessed,
        Title: "Property Knobs",
        Subtitle: "Live C# state mutation");

    private static Node RenderCodeAndIdentityInspector(GalleryAppState state, GalleryCatalogEntry selected)
    {
        var dynamicSnippet = BuildDynamicCodeSnippet(state, selected);
        return new Surface(
            [
                RenderIdentityPresetRow(state),
                new Row(
                [
                    new Button("c-digitalbrain-ui", "C# DigitalBrain.UI", Emit: AppSignals.Emit("gallery", "select", "tab:csharp"), Tone: state.CodeTab == "csharp" ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "code", Small = true },
                    new Button("flutter-ic", "Flutter Ic*", Emit: AppSignals.Emit("gallery", "select", "tab:flutter"), Tone: state.CodeTab == "flutter" ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "layers", Small = true },
                    new Button("contract-signal", "Contract & [Signal]", Emit: AppSignals.Emit("gallery", "select", "tab:contract"), Tone: state.CodeTab == "contract" ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "radio", Small = true },
                    new Button("fire-test-signal", "Fire Test [Signal]", Emit: AppSignals.Emit("gallery", "select", "pulse"), Tone: ButtonTone.Accent) { Icon = "zap", Small = true },
                    new Button("copy-snippet", "Copy Snippet", Intent: AppSignals.Copy(dynamicSnippet), Tone: ButtonTone.Neutral) { Icon = "copy", Small = true },
                ]) { Gap = 8 },
                new Surface([new Text(dynamicSnippet) { Mono = true, Tabular = true }], Kind: SurfaceKind.Acrylic),
            ],
            Kind: SurfaceKind.Recessed,
            Title: "C# App<TState> & [Signal] Contract",
            Subtitle: $"{selected.NeuronContract} · {selected.SourceNeuronUri}");
    }

    private static Node RenderIdentityPresetRow(GalleryAppState state) => new Row(
    [
        new Button("fluent-acrylic", "Fluent Acrylic", Emit: AppSignals.Emit("gallery", "switch-identity", "fluent-acrylic", "floating-glass"), Tone: state.StylePreset == "fluent-acrylic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "palette" },
        new Button("spatial-glass", "Spatial Glass", Emit: AppSignals.Emit("gallery", "switch-identity", "spatial-glass", "compact-pill"), Tone: state.StylePreset == "spatial-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sparkles" },
        new Button("cyber-synaptic", "Cyber Synaptic", Emit: AppSignals.Emit("gallery", "switch-identity", "cyber-synaptic", "split-top-dock"), Tone: state.StylePreset == "cyber-synaptic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "zap" },
        new Button("warm-paper", "Warm Paper", Emit: AppSignals.Emit("gallery", "switch-identity", "warm-paper", "classic-bottom"), Tone: state.StylePreset == "warm-paper" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
        new Button("warm-mica", "Warm Mica", Emit: AppSignals.Emit("gallery", "switch-identity", "fluent-warm-mica", "floating-glass"), Tone: state.StylePreset == "fluent-warm-mica" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
        new Button("linear-carbon", "Linear Carbon", Emit: AppSignals.Emit("gallery", "switch-identity", "linear-carbon", "classic-bottom"), Tone: state.StylePreset == "linear-carbon" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "terminal" },
        new Button("nordic-tactical", "Nordic Tactical", Emit: AppSignals.Emit("gallery", "switch-identity", "nordic-tactical", "split-top-dock"), Tone: state.StylePreset == "nordic-tactical" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "shield" },
    ])
    { Gap = 8 };

    private static Node RenderCatalogGridSection(GalleryAppState state, GalleryCatalogEntry[] visible)
    {
        if (visible.Length == 0)
        {
            return new InfoBar(
                "No components found",
                $"No UI Kit components matched '{state.Search}' in category '{state.Category}'.",
                Tone: "warning",
                Action: new Button("reset-filter", "Reset Filter", Emit: AppSignals.Emit("gallery", "filter", "all", ""), Tone: ButtonTone.Accent) { Small = true });
        }

        var cards = visible.Select(entry =>
        {
            var isSelected = string.Equals(entry.Id, state.SelectedId, StringComparison.OrdinalIgnoreCase);
            return (Node)new Surface(
                [
                    new Row([new TagPill(GalleryLibrary.CategoryShortBadge(entry.Category), isSelected ? "accent" : "neutral", entry.Icon), new TagPill(entry.Signals.FirstOrDefault() ?? "Signal", "done", "zap")]) { Gap = 6 },
                    new Text(entry.Summary) { Tone = "muted", Size = 12 },
                    new Text(entry.NeuronContract) { Mono = true, Tone = "subtle", Size = 11.5 },
                    new Button($"inspect-{entry.Id}", $"Inspect {entry.Title}", AppSignals.Emit("gallery", "select", entry.Id), Tone: isSelected ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = isSelected ? "check" : "arrow-up-right", Small = true },
                ],
                Kind: isSelected ? SurfaceKind.Elevated : SurfaceKind.Acrylic,
                Title: entry.Title,
                Subtitle: GalleryLibrary.CategoryDisplayName(entry.Category));
        }).ToArray();

        return new Surface([new Grid(3, cards, Gap: 12)], Kind: SurfaceKind.Acrylic, Title: $"Component Catalog Grid ({visible.Length} of {state.Entries.Length})", Subtitle: "Click any component card to inspect its live neuron preview and contract");
    }

    private static Node BuildSamplePreviewNode(GalleryAppState state, GalleryCatalogEntry selected)
    {
        var enabled = state.KnobState != "disabled";
        if (state.KnobState == "loading")
        {
            return new Column([new Progress($"Loading {selected.Title}…", 0.65, Detail: selected.NeuronContract), new InfoBar($"Streaming {selected.NeuronContract}", $"Awaiting grain projection from {selected.SourceNeuronUri}…", Tone: "info")]) { Gap = 12 };
        }
        if (state.KnobState == "empty")
        {
            return new InfoBar($"Empty state for {selected.Title}", $"No active items projected at {selected.SourceNeuronUri}.", Tone: "warning", Action: new Button("restore-interactive", "Restore Interactive", Emit: AppSignals.Emit("gallery", "select", "knob:state:interactive"), Tone: ButtonTone.Accent) { Small = true });
        }

        var badgeTone = MapKnobBadgeTone(state.KnobTone);
        var btnTone = state.KnobStyle == "primary" ? ButtonTone.Accent : ButtonTone.Neutral;

        return selected.Id switch
        {
            "themes" => new Column([new Text($"Active Identity: {state.StylePreset} · BarLayout: {state.BarLayout}") { Tone = "strong", Tabular = true }, RenderIdentityPresetRow(state)]) { Gap = 12 },
            "typography" => new Column([new Heading("InterDisplay 32 · 1,492,048 tok", Level: 1, Subtitle: "Inter Body 14 · Tabular numerals (tnum) keep live telemetry aligned."), new Text("neuron://local/apps/ino · 99.98% SLA · 12.4ms p95 · 142 sig/min") { Mono = true, Tabular = true, Tone = "accent" }]) { Gap = 10 },
            "iconography" or "stat-card" => new Grid(3, [Ui.StatCard("kpi-signals", "Active Signals / min", $"{142 + state.SignalPulseCount}", "+18.4%", badgeTone, "zap"), Ui.StatCard("kpi-tokens", "Compute Throughput", "1,248,920 tok", "+14.2%", BadgeTone.Done, "cpu"), Ui.StatCard("kpi-latency", "P95 Silo Latency", "6.8 ms", "-1.2 ms", BadgeTone.Accent, "activity")], Gap: 10),
            "acrylic-surface" or "surfaces" => new Grid(3, [new Surface([new Text("24px backdrop blur + 1px top-lit specular border.")], Kind: SurfaceKind.Acrylic, Title: "Acrylic Surface", Subtitle: $"Preset: {state.StylePreset}"), new Surface([new Text("Raised modal & card container.")], Kind: SurfaceKind.Elevated, Title: "Elevated Surface"), new Surface([new Text("Sunk inspector well for code snippets.")], Kind: SurfaceKind.Recessed, Title: "Recessed Surface")], Gap: 10),
            "buttons" => new Row(new[] { "primary", "secondary", "ghost", "danger" }.Select(style => (Node)new Button(style, char.ToUpperInvariant(style[0]) + style[1..], Emit: AppSignals.Emit("gallery", "select", "pulse"), Tone: style == "primary" ? ButtonTone.Accent : btnTone) { Style = style, Small = state.KnobSmall, Enabled = enabled, Icon = "sparkles" }).ToArray()) { Gap = 10 },
            "inputs" => new Column([new Field("neuron-address", "neuron-address", FieldKind.Text, "Enter neuron:// address…", AppSignals.Emit("gallery", "select", "pulse")), new NeuronRef("ino", "neuron://local/apps/ino")]) { Gap = 12 },
            "switch" => new Row([new Switch("stream", state.SwitchValue ? "Live [Signal] stream active" : "Stream paused", IsOn: state.SwitchValue, Emit: enabled ? AppSignals.Emit("gallery", "select", "toggle:switch") : null, Locked: state.SwitchLocked), new Button("lock-policy", state.SwitchLocked ? "Unlock policy" : "Lock policy", AppSignals.Emit("gallery", "select", "toggle:lock"), Tone: state.SwitchLocked ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "lock", Small = true }]) { Gap = 12 },
            "select" => new Select("Active Reasoning Model", state.SelectedOption, [new SelectOption("option-claude-sonnet-4-5", "claude-sonnet-4-5", "Claude Sonnet 4.5", AppSignals.Emit("gallery", "select", "option:claude-sonnet-4-5")), new SelectOption("option-gpt-5-thinking", "gpt-5-thinking", "GPT-5 Thinking", AppSignals.Emit("gallery", "select", "option:gpt-5-thinking"))]),
            "tag-pill" => new Row([new TagPill("NEUTRAL", "neutral", "sparkles"), new TagPill("ACCENT", "accent", "zap"), new TagPill("DONE", "done", "check"), new TagPill("WORKING", "working", "activity"), new TagPill("ERROR", "error", "triangle-alert")]) { Gap = 8 },
            "progress-infobar" => new Column([new Progress("Orleans Silo Grain Warmup", 0.72, Detail: "14 / 14 [Scenario] gates green (72%)"), new InfoBar("Orleans grain pool synced 14/14 [Scenario] gates.", "Zero-leakage capability broker verified all active neuron bindings.", Tone: "info", Action: new Button("pulse-gate", "Pulse Gate", Emit: AppSignals.Emit("gallery", "select", "pulse"), Tone: ButtonTone.Accent) { Small = true, Icon = "zap" })]) { Gap = 12 },
            "app-shell" => Ui.AppShell("gallery-shell-preview", sidebar: new Column([Ui.NavItem("nav-1", "Overview", "layout-grid", "select:shell:overview", selected: state.ShellNav == "overview"), Ui.NavItem("nav-2", "Signals", "radio", "select:shell:signals", selected: state.ShellNav == "signals", badge: $"{14 + state.SignalPulseCount}")]) { Gap = 6 }, body: Ui.StatCard("stat-1", "Active Signals / min", $"{142 + state.SignalPulseCount}", "+18.4%", badgeTone, "zap")),
            "tabs" => new Tabs([new TabItem("overview", "overview", "OVERVIEW", AppSignals.Emit("gallery", "select", "shell:overview")), new TabItem("signals", "signals", "TELEMETRY", AppSignals.Emit("gallery", "select", "shell:signals")), new TabItem("contracts", "contracts", "CONTRACTS", AppSignals.Emit("gallery", "select", "shell:contracts"))], state.ShellNav),
            "approval-card" or "change-preview-card" => new ApprovalCardNode("card-gallery-1", "card-gallery-1", "Approve storage write to /work/notes.md?", "Confirmation required by storage capability policy (scope:neuron://local/apps/ino#card-1)", [new ApprovalRowNode("Target Path", "/work/draft.md", "/work/notes.md"), new ApprovalRowNode("Scope", "Read-Only", "Read/Write")], SystemName: "Storage", Status: state.ApprovalStatus, SourceNeuronUri: selected.SourceNeuronUri),
            "table-card" => new DataTable("Telemetry Spans — Live Silo", ["Span", "Status", "Duration", "Module"], [["ino.send", "OK", "12 ms", "Apps"], ["synapse.check", "OK", "118 ms", "Creator"], ["storage.stat", "OK", "4 ms", "Identity"]], SourceNeuronUri: selected.SourceNeuronUri, From: "Postgres"),
            "chart-card" => new Chart(state.KnobChartKind == "line" ? "line" : "bar", "Throughput per month, 2026", [new ChartPointNode("Jan", 68), new ChartPointNode("Feb", 82), new ChartPointNode("Mar", 94), new ChartPointNode("Apr", 128 + state.SignalPulseCount)], ComparePoints: [54, 62, 71, 88], SeriesName: "2026", CompareName: "2025", SourceNeuronUri: selected.SourceNeuronUri, From: "Analytics"),
            "choice-card" => new ChoiceCardNode("choice-gallery-1", "Which workspace should receive the deployment digest?", [new ChoiceOptionNode("Primary Workspace", "neuron://local/circles/primary", new Button("pick-0", "Primary Workspace", Emit: AppSignals.Emit("gallery", "select", "card:choice:0"))), new ChoiceOptionNode("Engineering Circle", "neuron://local/circles/eng", new Button("pick-1", "Engineering Circle", Emit: AppSignals.Emit("gallery", "select", "card:choice:1")))], PickedIndex: state.ChoicePickedIndex),
            "draft-card" => new DraftCardNode("draft-gallery-1", "email", "user@intochat.os", "team@intochat.os", "Confirmed: release schedule updated.", Subject: "Release Schedule Confirmation", Status: state.DraftStatus, Send: new Button("send", "Send", Emit: AppSignals.Emit("gallery", "select", "card:draft:sent"), Tone: ButtonTone.Accent), Discard: new Button("discard", "Discard", Emit: AppSignals.Emit("gallery", "select", "card:draft:pending"))),
            "secret-card" => new SecretCardNode("GitHub API Token", "Required to read repository issues. Stored encrypted in your personal vault.", SavedIn: "GITHUB_TOKEN", Status: state.SecretStatus, Enter: new Button("enter", "Save to vault", Emit: AppSignals.Emit("gallery", "select", "card:secret:saved"), Tone: ButtonTone.Accent), Skip: new Button("skip", "Skip", Emit: AppSignals.Emit("gallery", "select", "card:secret:pending"))),
            "job-card" => new JobCardNode("Verifying BDD [Scenario] Gate & Deploying Smart App", [new JobStepNode("Compile C# App<TState> grain", "done", "118ms"), new JobStepNode("Execute [Scenario] BDD assertions", "done", "14/14 green"), new JobStepNode("Publish neuron://local/apps/unit", state.JobStatus == "done" ? "done" : "running", "Streaming…")], Progress: state.JobStatus == "done" ? 1.0 : 0.75, Status: state.JobStatus, ResultText: "1.4s elapsed", Cancel: new Button("cancel", "Cancel", Emit: AppSignals.Emit("gallery", "select", state.JobStatus == "done" ? "card:job:running" : "card:job:done"))),
            "error-line" => new InfoBar("Upstream endpoint did not answer in time — nothing was changed yet", "Timeout after 10s on outbound capability call. Click Retry to run the request again.", Tone: "danger", Action: new Button("retry-request", "Retry Request", Emit: AppSignals.Emit("gallery", "select", "pulse"), Tone: ButtonTone.Accent) { Small = true, Icon = "refresh" }),
            "brain-scene-3d" => new BrainSceneNode([new BrainGraphNode("os", "OperatingSystem", "neuron://local/os", "core", 0, 0, 0, "working") { Subtitle = "Orleans Silo Kernel", Select = new Button("node-os", "os", Emit: AppSignals.Emit("gallery", "select", "node:os")) }, new BrainGraphNode("ino", "Ino Agent", "neuron://local/apps/ino", "agent", 120, 40, -20, "working") { Subtitle = "Personal AI Coordinator", Select = new Button("node-ino", "ino", Emit: AppSignals.Emit("gallery", "select", "node:ino")) }, new BrainGraphNode("creator", "Creator 3D", "neuron://local/apps/creator", "studio", -95, 75, -45, "working") { Subtitle = "Self-Improving Roslyn Studio", Select = new Button("node-creator", "creator", Emit: AppSignals.Emit("gallery", "select", "node:creator")) }], [new BrainGraphSynapse("os", "ino", "InoSend", true), new BrainGraphSynapse("os", "creator", "CreatorInspect", true)], SelectedId: state.SelectedBrainNode),
            "files-neuron-uri" or "neuron-paths" => new Column([new NeuronRef("files", "neuron://local/storage.files/work/context.md"), new NeuronRef("table", "neuron://local/postgres/main#query-result"), new NeuronRef("view", "neuron://local/apps/gallery/view")]) { Gap = 10 },
            _ => new RecordCardNode(selected.Category, selected.Title, [new RecordFieldNode("Contract", selected.NeuronContract), new RecordFieldNode("Neuron URI", selected.SourceNeuronUri), new RecordFieldNode("Signals", string.Join(", ", selected.Signals))], Subtitle: selected.Summary, LinkLabel: state.ApprovalStatus == "approved" ? "Saved" : "Inspect", Open: new Button("open", state.ApprovalStatus == "approved" ? "Saved" : "Inspect", Emit: AppSignals.Emit("gallery", "select", "card:approval:approved"))),
        };
    }

    private static string BuildDynamicCodeSnippet(GalleryAppState state, GalleryCatalogEntry selected) => state.CodeTab switch
    {
        "flutter" => $"// NeuronView -> _ImmutableView ({state.StylePreset} · {state.BarLayout})\nIcAcrylicBox(\n  padding: const EdgeInsets.all({(state.KnobSmall ? 14 : 20)}),\n  child: Btn(label: '{selected.Title}', primary: {(state.KnobStyle == "primary" ? "true" : "false")}, small: {(state.KnobSmall ? "true" : "false")}),\n)",
        "contract" => $"Contract: {selected.NeuronContract}\nURI:      {selected.SourceNeuronUri}\nSignals:  [Signal] {string.Join(", ", selected.Signals)} (fired {state.SignalPulseCount} time(s))\nIdentity: {state.StylePreset} ({state.BarLayout})",
        _ => $"// {selected.NeuronContract} · [{string.Join(", ", selected.Signals)}]\n{selected.CSharpSample}",
    };

    private static Button KnobButton(string label, string selectArg, bool active) => new(selectArg, label, AppSignals.Emit("gallery", "select", selectArg), Tone: active ? ButtonTone.Accent : ButtonTone.Neutral) { Style = active ? "primary" : "secondary", Small = true };

    private static BadgeTone MapKnobBadgeTone(string tone) => tone.ToLowerInvariant() switch
    {
        "done" or "ok" or "success" => BadgeTone.Done,
        "working" or "warn" or "warning" => BadgeTone.Working,
        "error" or "danger" => BadgeTone.Error,
        "neutral" => BadgeTone.Neutral,
        _ => BadgeTone.Accent,
    };
}
