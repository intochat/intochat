using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Gallery;

internal static class GalleryRendering
{
    public static View RenderSide(GalleryAppState state)
    {
        var visible = GalleryLibrary.FilteredEntries(state.Entries, state.Category, state.Search);
        var designCount = state.Entries.Count(e => e.Category == "design");
        var controlsCount = state.Entries.Count(e => e.Category == "controls");
        var layoutCount = state.Entries.Count(e => e.Category == "layout");
        var lfpCount = state.Entries.Count(e => e.Category == "chat-lfp");
        var brainCount = state.Entries.Count(e => e.Category == "3d-brain");

        var categoryButtons = new Node[]
        {
            Ui.NavItem("cat-home", "Catalog Grid (Home)", "house", "select:home", selected: state.ViewMode == "grid", badge: $"{state.Entries.Length}"),
            Ui.NavItem("cat-all", "All Controls", "layout-grid", "filter:all", selected: state.Category == "all" && state.ViewMode != "grid", badge: $"{state.Entries.Length}"),
            Ui.NavItem("cat-design", "Design & Identity", "palette", "filter:design", selected: state.Category == "design", badge: $"{designCount}"),
            Ui.NavItem("cat-controls", "Basic Input", "mouse-pointer-click", "filter:controls", selected: state.Category == "controls", badge: $"{controlsCount}"),
            Ui.NavItem("cat-layout", "Layout & App Shell", "panel-left", "filter:layout", selected: state.Category == "layout", badge: $"{layoutCount}"),
            Ui.NavItem("cat-lfp", "Data & 13 LFP Chat Cards", "table", "filter:chat-lfp", selected: state.Category == "chat-lfp", badge: $"{lfpCount}"),
            Ui.NavItem("cat-3d", "3D Brain & Neurons", "brain-circuit", "filter:3d-brain", selected: state.Category == "3d-brain", badge: $"{brainCount}"),
        };

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
                        new Row(
                        [
                            new TagPill(state.StylePreset, "accent", "palette"),
                            new TagPill(state.BarLayout, "neutral", "layout"),
                        ]) { Gap = 6 },
                        new Text($"Identity: {state.StylePreset} · {state.BarLayout}") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "UI Gallery · Component Studio"),
                new TextField("gallery-search", state.Search, "Search 5 categories & LFP cards…")
                {
                    OnSubmit = new ButtonAction("gallery", "filter", [state.Category, state.Search]),
                },
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
            return new(
                "UI Gallery",
                new Column(
                [
                    RenderStudioHeroBanner(state, selected),
                    RenderCatalogGridSection(state, visible),
                ])
                { Gap = 16 });
        }

        var livePreview = BuildSamplePreviewNode(state, selected);
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
                            new Button("Back to Gallery", new ButtonAction("gallery", "select", ["home"]), Tone: ButtonTone.Neutral)
                            {
                                Icon = "arrow-left",
                                Small = true,
                            },
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
                        [
                            livePreview,
                        ],
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
            new Heading(
                "IntoChat UI Gallery · Single-Format Component Studio",
                Level: 1,
                Subtitle: "7 UI Identities · Squircle IcAppIcon · Ergonomic C# DigitalBrain.UI Builders · 13 Interactive LFP Chat Cards · 3D Neural Graph"),
            new Row(
            [
                new TagPill($"{state.Entries.Length} Components", "accent", "sparkles"),
                new TagPill("13 LFP Chat Cards", "done", "table"),
                new TagPill($"{state.StylePreset} · {state.BarLayout}", "neutral", "palette"),
                new Button("Inspect Selected Control", new ButtonAction("gallery", "select", [selected.Id]), Tone: ButtonTone.Accent)
                {
                    Icon = "arrow-up-right",
                    Small = true,
                },
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
            new Row(
            [
                KnobButton("Primary", "knob:style:primary", state.KnobStyle == "primary"),
                KnobButton("Secondary", "knob:style:secondary", state.KnobStyle == "secondary"),
                KnobButton("Ghost", "knob:style:ghost", state.KnobStyle == "ghost"),
                KnobButton("Danger", "knob:style:danger", state.KnobStyle == "danger"),
            ]) { Gap = 6 },
            new Text("TONE (Neutral · Accent · Done · Working · Error)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Neutral", "knob:tone:neutral", state.KnobTone == "neutral"),
                KnobButton("Accent", "knob:tone:accent", state.KnobTone == "accent"),
                KnobButton("Done", "knob:tone:done", state.KnobTone == "done"),
                KnobButton("Working", "knob:tone:working", state.KnobTone == "working"),
                KnobButton("Error", "knob:tone:error", state.KnobTone == "error"),
            ]) { Gap = 6 },
            new Text("SIZE (Standard 36px · Small 30px)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Standard (36px)", "knob:size:standard", !state.KnobSmall),
                KnobButton("Small (30px)", "knob:size:small", state.KnobSmall),
            ]) { Gap = 6 },
            new Text("STATE (Interactive · Loading · Disabled · Empty)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Interactive", "knob:state:interactive", state.KnobState == "interactive"),
                KnobButton("Loading", "knob:state:loading", state.KnobState == "loading"),
                KnobButton("Disabled", "knob:state:disabled", state.KnobState == "disabled"),
                KnobButton("Empty", "knob:state:empty", state.KnobState == "empty"),
            ]) { Gap = 6 },
            new Text("SURFACE (Acrylic · Elevated · Recessed)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Acrylic", "knob:surface:acrylic", state.KnobSurface == "acrylic"),
                KnobButton("Elevated", "knob:surface:elevated", state.KnobSurface == "elevated"),
                KnobButton("Recessed", "knob:surface:recessed", state.KnobSurface == "recessed"),
            ]) { Gap = 6 },
            new Text("CHART KIND (Bar · Line)") { Tone = "muted", Size = 11 },
            new Row(
            [
                KnobButton("Bar", "knob:chart:bar", state.KnobChartKind == "bar"),
                KnobButton("Line", "knob:chart:line", state.KnobChartKind == "line"),
            ]) { Gap = 6 },
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
                    new Button("C# DigitalBrain.UI", new ButtonAction("gallery", "select", ["tab:csharp"]), Tone: state.CodeTab == "csharp" ? ButtonTone.Accent : ButtonTone.Neutral)
                    {
                        Icon = "code",
                        Small = true,
                    },
                    new Button("Flutter Ic*", new ButtonAction("gallery", "select", ["tab:flutter"]), Tone: state.CodeTab == "flutter" ? ButtonTone.Accent : ButtonTone.Neutral)
                    {
                        Icon = "layers",
                        Small = true,
                    },
                    new Button("Contract & [Signal]", new ButtonAction("gallery", "select", ["tab:contract"]), Tone: state.CodeTab == "contract" ? ButtonTone.Accent : ButtonTone.Neutral)
                    {
                        Icon = "radio",
                        Small = true,
                    },
                    new Button("Fire Test [Signal]", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent)
                    {
                        Icon = "zap",
                        Small = true,
                    },
                    new Button("Copy Snippet", new ButtonAction("gallery", "copy", [dynamicSnippet]), Tone: ButtonTone.Neutral)
                    {
                        Icon = "copy",
                        Small = true,
                    },
                ]) { Gap = 8 },
                new Surface(
                    [
                        new Text(dynamicSnippet) { Mono = true, Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic),
            ],
            Kind: SurfaceKind.Recessed,
            Title: "C# App<TState> & [Signal] Contract",
            Subtitle: $"{selected.NeuronContract} · {selected.SourceNeuronUri}");
    }

    private static Node RenderIdentityPresetRow(GalleryAppState state) => new Row(
    [
        new Button("Fluent Acrylic", new ButtonAction("gallery", "switch-identity", ["fluent-acrylic", "floating-glass"]), Tone: state.StylePreset == "fluent-acrylic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "palette" },
        new Button("Spatial Glass", new ButtonAction("gallery", "switch-identity", ["spatial-glass", "compact-pill"]), Tone: state.StylePreset == "spatial-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sparkles" },
        new Button("Cyber Synaptic", new ButtonAction("gallery", "switch-identity", ["cyber-synaptic", "split-top-dock"]), Tone: state.StylePreset == "cyber-synaptic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "zap" },
        new Button("Warm Paper", new ButtonAction("gallery", "switch-identity", ["warm-paper", "classic-bottom"]), Tone: state.StylePreset == "warm-paper" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
        new Button("Warm Mica", new ButtonAction("gallery", "switch-identity", ["fluent-warm-mica", "floating-glass"]), Tone: state.StylePreset == "fluent-warm-mica" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "sun" },
        new Button("Linear Carbon", new ButtonAction("gallery", "switch-identity", ["linear-carbon", "classic-bottom"]), Tone: state.StylePreset == "linear-carbon" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "terminal" },
        new Button("Nordic Tactical", new ButtonAction("gallery", "switch-identity", ["nordic-tactical", "split-top-dock"]), Tone: state.StylePreset == "nordic-tactical" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = "shield" },
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
                Action: new Button("Reset Filter", new ButtonAction("gallery", "filter", ["all", ""]), Tone: ButtonTone.Accent) { Small = true });
        }

        var cards = visible
            .Select(entry =>
            {
                var isSelected = string.Equals(entry.Id, state.SelectedId, StringComparison.OrdinalIgnoreCase);
                return (Node)new Surface(
                    [
                        new Row(
                        [
                            new TagPill(GalleryLibrary.CategoryShortBadge(entry.Category), isSelected ? "accent" : "neutral", entry.Icon),
                            new TagPill(entry.Signals.FirstOrDefault() ?? "Signal", "done", "zap"),
                        ]) { Gap = 6 },
                        new Text(entry.Summary) { Tone = "muted", Size = 12 },
                        new Text(entry.NeuronContract) { Mono = true, Tone = "subtle", Size = 11.5 },
                        new Button(
                            $"Inspect {entry.Title}",
                            new ButtonAction("gallery", "select", [entry.Id]),
                            Tone: isSelected ? ButtonTone.Accent : ButtonTone.Neutral)
                        {
                            Icon = isSelected ? "check" : "arrow-up-right",
                            Small = true,
                        },
                    ],
                    Kind: isSelected ? SurfaceKind.Elevated : SurfaceKind.Acrylic,
                    Title: entry.Title,
                    Subtitle: GalleryLibrary.CategoryDisplayName(entry.Category));
            })
            .ToArray();

        return new Surface(
            [
                new Grid(3, cards, Gap: 12),
            ],
            Kind: SurfaceKind.Acrylic,
            Title: $"Component Catalog Grid ({visible.Length} of {state.Entries.Length})",
            Subtitle: "Click any component card to inspect its live neuron preview and contract");
    }

    private static Node BuildSamplePreviewNode(GalleryAppState state, GalleryCatalogEntry selected)
    {
        var enabled = state.KnobState != "disabled";
        if (state.KnobState == "loading")
        {
            return new Column(
            [
                new Progress($"Loading {selected.Title}…", 0.65, Detail: selected.NeuronContract),
                new InfoBar($"Streaming {selected.NeuronContract}", $"Awaiting grain projection from {selected.SourceNeuronUri}…", Tone: "info"),
            ])
            { Gap = 12 };
        }

        if (state.KnobState == "empty")
        {
            return new InfoBar(
                $"Empty state for {selected.Title}",
                $"No active items projected at {selected.SourceNeuronUri}. Switch the State knob back to Interactive to populate.",
                Tone: "warning",
                Action: new Button("Restore Interactive", new ButtonAction("gallery", "select", ["knob:state:interactive"]), Tone: ButtonTone.Accent) { Small = true });
        }

        var badgeTone = MapKnobBadgeTone(state.KnobTone);
        var btnTone = state.KnobStyle == "primary"
            ? ButtonTone.Accent
            : ButtonTone.Neutral;

        return selected.Id switch
        {
            "themes" => new Column(
            [
                new Text($"Active Identity: {state.StylePreset} · BarLayout: {state.BarLayout}") { Tone = "strong", Tabular = true },
                RenderIdentityPresetRow(state),
                new Grid(
                    3,
                    [
                        Ui.StatCard("theme-blur", "Backdrop Blur", state.StylePreset == "warm-paper" ? "0px (Matte)" : "24px Sigma", "Glass", badgeTone, "layers"),
                        Ui.StatCard("theme-dock", "Dock Topology", state.BarLayout, "Adaptive", BadgeTone.Accent, "layout"),
                        Ui.StatCard("theme-shader", "3D Brain Shader", state.StylePreset, "60 FPS", BadgeTone.Done, "brain-circuit"),
                    ],
                    Gap: 10),
            ])
            { Gap = 12 },

            "typography" => new Column(
            [
                new Heading("InterDisplay 32 · 1,492,048 tok", Level: 1, Subtitle: "Inter Body 14 · Tabular numerals (tnum) keep live telemetry aligned across frames."),
                new Text("neuron://nordlys/apps.ino/main · 99.98% SLA · 12.4ms p95 · 142 sig/min") { Mono = true, Tabular = true, Tone = "accent" },
                new Row(
                [
                    new TagPill("Inter Regular 400", "neutral", "type"),
                    new TagPill("Inter SemiBold 600", "accent", "type"),
                    new TagPill("InterDisplay Bold 700 (tnum)", "done", "sparkles"),
                ]) { Gap = 8 },
            ])
            { Gap = 10 },

            "iconography" => new Grid(
                3,
                [
                    Ui.StatCard("ico-ino", "Ino Assistant", "#3D7EFF", "bot", BadgeTone.Accent, "sparkles"),
                    Ui.StatCard("ico-creator", "Creator Studio", "#FF5D8F", "brain-circuit", BadgeTone.Done, "brain-circuit"),
                    Ui.StatCard("ico-files", "Files Finder", "#F6B73C", "folder", BadgeTone.Accent, "folder"),
                    Ui.StatCard("ico-gallery", "UI Gallery", "#22C55E", "widgets", BadgeTone.Done, "palette"),
                    Ui.StatCard("ico-photocraft", "PhotoCraft", "#EC4899", "image", BadgeTone.Working, "image"),
                    Ui.StatCard("ico-settings", "Settings", "#64748B", "settings", BadgeTone.Neutral, "settings"),
                ],
                Gap: 10),

            "acrylic-surface" or "surfaces" => new Grid(
                3,
                [
                    new Surface(
                        [
                            new Text("24px backdrop blur + 1px top-lit specular hairline border.") { Tone = "muted" },
                            new TagPill("SurfaceKind.Acrylic", state.KnobTone, "layers"),
                        ],
                        Kind: SurfaceKind.Acrylic,
                        Title: "Acrylic Surface",
                        Subtitle: $"Preset: {state.StylePreset}"),
                    new Surface(
                        [
                            new Text("Raised modal & card container with ambient drop shadow.") { Tone = "muted" },
                            new TagPill("SurfaceKind.Elevated", "accent", "square-stack"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "Elevated Surface",
                        Subtitle: "14px Card Radius"),
                    new Surface(
                        [
                            new Text("Sunk inspector well for code snippets, logs, and property knobs.") { Tone = "muted" },
                            new TagPill("SurfaceKind.Recessed", "neutral", "terminal"),
                        ],
                        Kind: SurfaceKind.Recessed,
                        Title: "Recessed Surface",
                        Subtitle: "1px Hairline Border"),
                ],
                Gap: 10),

            "buttons" => new Column(
            [
                new Row(
                [
                    new Button($"Active Knob ({state.KnobStyle})", new ButtonAction("gallery", "select", ["pulse"]), Tone: btnTone)
                    {
                        Style = state.KnobStyle,
                        Small = state.KnobSmall,
                        Enabled = enabled,
                        Icon = "sparkles",
                    },
                    new Button("Primary", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent)
                    {
                        Style = "primary",
                        Small = state.KnobSmall,
                        Enabled = enabled,
                        Icon = "rocket",
                    },
                    new Button("Secondary", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Neutral)
                    {
                        Style = "secondary",
                        Small = state.KnobSmall,
                        Enabled = enabled,
                        Icon = "sliders-horizontal",
                    },
                    new Button("Ghost", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Neutral)
                    {
                        Style = "ghost",
                        Small = state.KnobSmall,
                        Enabled = enabled,
                        Icon = "eye",
                    },
                    new Button("Danger", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Neutral)
                    {
                        Style = "danger",
                        Small = state.KnobSmall,
                        Enabled = enabled,
                        Icon = "trash",
                    },
                ]) { Gap = 10 },
                new Text($"Height: {(state.KnobSmall ? "30px (Small)" : "36px (Standard)")} · Enabled: {enabled} · Pulses fired: {state.SignalPulseCount}") { Tone = "muted", Tabular = true },
            ])
            { Gap = 12 },

            "inputs" => new Column(
            [
                new TextField("neuron-address", "neuron://nordlys/apps.ino/main", "Enter neuron:// address…")
                {
                    OnSubmit = new ButtonAction("gallery", "select", ["pulse"]),
                },
                new NeuronRef("ino", "neuron://nordlys/apps.ino/main"),
            ])
            { Gap = 12 },

            "switch" => new Row(
            [
                new Switch(
                    state.SwitchValue ? "Live [Signal] stream active" : "Stream paused",
                    IsOn: state.SwitchValue,
                    Action: enabled ? new ButtonAction("gallery", "select", ["toggle:switch"]) : null,
                    Locked: state.SwitchLocked),
                new Button(
                    state.SwitchLocked ? "Unlock policy" : "Lock policy",
                    new ButtonAction("gallery", "select", ["toggle:lock"]),
                    Tone: state.SwitchLocked ? ButtonTone.Accent : ButtonTone.Neutral)
                {
                    Icon = "lock",
                    Small = true,
                },
            ])
            { Gap = 12 },

            "select" => new Select(
                "Active Reasoning Model",
                state.SelectedOption,
                [
                    new SelectOption("claude-sonnet-4-5", "Claude Sonnet 4.5", new ButtonAction("gallery", "select", ["option:claude-sonnet-4-5"])),
                    new SelectOption("gpt-5-thinking", "GPT-5 Thinking", new ButtonAction("gallery", "select", ["option:gpt-5-thinking"])),
                    new SelectOption("gemini-2.5-pro", "Gemini 2.5 Pro", new ButtonAction("gallery", "select", ["option:gemini-2.5-pro"])),
                    new SelectOption("qwen3.5:4b", "Qwen 3.5 4B (Local)", new ButtonAction("gallery", "select", ["option:qwen3.5:4b"])),
                ]),

            "tag-pill" => new Row(
            [
                new TagPill("NEUTRAL BADGE", "neutral", "sparkles"),
                new TagPill("ACCENT BADGE", "accent", "zap"),
                new TagPill("DONE BADGE", "done", "check"),
                new TagPill("WORKING BADGE", "working", "activity"),
                new TagPill("ERROR BADGE", "error", "triangle-alert"),
            ])
            { Gap = 8 },

            "progress-infobar" => new Column(
            [
                new Progress("Orleans Silo Grain Warmup", 0.72, Detail: "14 / 14 [Scenario] gates green (72%)"),
                new InfoBar(
                    "Orleans grain pool synced 14/14 [Scenario] gates.",
                    "Zero-leakage capability broker verified all active neuron bindings.",
                    Tone: state.KnobTone switch
                    {
                        "error" => "danger",
                        "working" => "warning",
                        "done" => "success",
                        _ => "info",
                    },
                    Action: new Button("Pulse Gate", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent) { Small = true, Icon = "zap" }),
            ])
            { Gap = 12 },

            "app-shell" => Ui.AppShell(
                "gallery-shell-preview",
                sidebar: new Column(
                [
                    Ui.NavItem("nav-1", "Overview", "layout-grid", "select:shell:overview", selected: state.ShellNav == "overview"),
                    Ui.NavItem("nav-2", "Signals", "radio", "select:shell:signals", selected: state.ShellNav == "signals", badge: $"{14 + state.SignalPulseCount}"),
                    Ui.NavItem("nav-3", "Contracts", "cpu", "select:shell:contracts", selected: state.ShellNav == "contracts"),
                ])
                { Gap = 6 },
                body: new Column(
                [
                    Ui.StatCard("stat-1", "Active Signals / min", $"{142 + state.SignalPulseCount}", "+18.4%", badgeTone, "zap"),
                    new Text($"Active AppShell pane: {state.ShellNav.ToUpperInvariant()}") { Tone = "muted", Tabular = true },
                ])
                { Gap = 10 },
                toolbar: new Row(
                [
                    new TagPill("DigitalBrain.UI AppShell", "accent", "panel-left"),
                    new TagPill($"Pane: {state.ShellNav}", "done"),
                ])
                { Gap = 8 }),

            "stat-card" => new Grid(
                3,
                [
                    Ui.StatCard("kpi-signals", "Active Signals / min", $"{142 + state.SignalPulseCount}", "+18.4%", badgeTone, "zap"),
                    Ui.StatCard("kpi-tokens", "Compute Throughput", "1,248,920 tok", "+14.2%", BadgeTone.Done, "cpu"),
                    Ui.StatCard("kpi-latency", "P95 Silo Latency", "6.8 ms", "-1.2 ms", BadgeTone.Accent, "activity"),
                ],
                Gap: 10),

            "tabs" => new Column(
            [
                new Tabs(
                    [
                        new TabItem("overview", "OVERVIEW", new ButtonAction("gallery", "select", ["shell:overview"])),
                        new TabItem("signals", "TELEMETRY", new ButtonAction("gallery", "select", ["shell:signals"])),
                        new TabItem("contracts", "CONTRACTS", new ButtonAction("gallery", "select", ["shell:contracts"])),
                    ],
                    state.ShellNav),
                new Surface(
                    [
                        new Text($"Active tab pane: {state.ShellNav} (tnum {1024 + state.SignalPulseCount} signals/s)") { Tone = "strong", Tabular = true },
                    ],
                    Kind: SurfaceKind.Recessed),
            ])
            { Gap = 12 },

            "approval-card" => new ApprovalCardNode(
                "card-gallery-1",
                "Update close date on Nordlys Q4 renewal to 15 Nov 2026?",
                "Ino matched Kari Holm's email to your open Salesforce opportunity (scope:neuron://local/apps/ino#card-1)",
                [
                    new ApprovalRowNode("Nordlys Q4 Enterprise", "30 Oct 2026", "15 Nov 2026", "480,000 NOK"),
                    new ApprovalRowNode("Owner", "Unassigned", "Vlad Horb"),
                ],
                SystemName: "Salesforce",
                Status: state.ApprovalStatus,
                OnApprove: new ButtonAction("gallery", "select", ["card:approval:approved"]),
                OnReject: new ButtonAction("gallery", "select", ["card:approval:rejected"]),
                SourceNeuronUri: selected.SourceNeuronUri),

            "table-card" => new DataTable(
                "Opportunities — Q4 2026",
                ["Account", "Stage", "Close Date", "Amount (NOK)", "Owner"],
                [
                    ["Nordlys Enterprise", "Closed Won", "15 Nov", "480,000", "Vlad"],
                    ["Kystverket Cloud", "Commit", "02 Dec", "920,000", "Aria"],
                    ["Bergen Maritime", "Review", "19 Oct", "310,000", "Devon"],
                ],
                SourceNeuronUri: selected.SourceNeuronUri,
                From: "Postgres"),

            "chart-card" => new Chart(
                state.KnobChartKind == "line" ? "line" : "bar",
                "Won per month, 2026",
                [
                    new ChartPointNode("Jan", 68),
                    new ChartPointNode("Feb", 82),
                    new ChartPointNode("Mar", 94),
                    new ChartPointNode("Apr", 128 + state.SignalPulseCount),
                ],
                ComparePoints: [54, 62, 71, 88],
                SeriesName: "2026",
                CompareName: "2025",
                SourceNeuronUri: selected.SourceNeuronUri,
                From: "Analytics",
                Prefix: "$"),

            "choice-card" => new ChoiceCardNode(
                "choice-gallery-1",
                "Which Nordlys workspace should receive the Q4 renewal brief?",
                [
                    new ChoiceOptionNode("Nordlys Enterprise (Primary)", "neuron://nordlys/circles/enterprise", new ButtonAction("gallery", "select", ["card:choice:0"])),
                    new ChoiceOptionNode("Executive Board Circle", "neuron://nordlys/circles/board", new ButtonAction("gallery", "select", ["card:choice:1"])),
                    new ChoiceOptionNode("Nordic Finance Ops", "neuron://nordlys/circles/finance", new ButtonAction("gallery", "select", ["card:choice:2"])),
                ],
                PickedIndex: state.ChoicePickedIndex),

            "draft-card" => new DraftCardNode(
                "draft-gallery-1",
                "email",
                "jeff@intochat.os",
                "Kari Holm <kari@nordlys.no>",
                "Hi Kari,\nConfirmed: we updated the Q4 renewal close date to 15 Nov 2026 (480,000 NOK).",
                Subject: "Nordlys Q4 Enterprise Renewal — 15 Nov confirmation",
                Status: state.DraftStatus,
                OnSend: new ButtonAction("gallery", "select", ["card:draft:sent"]),
                OnDiscard: new ButtonAction("gallery", "select", ["card:draft:pending"])),

            "change-preview-card" => new ApprovalCardNode(
                "change-gallery-1",
                "Apply 2 CRM field changes on Nordlys Q4 Enterprise",
                "Nordlys Q4 Enterprise",
                [
                    new ApprovalRowNode("Close date", "30 Oct 2026", "15 Nov 2026"),
                    new ApprovalRowNode("Amount", "450,000 NOK", "480,000 NOK"),
                ],
                SystemName: "Salesforce",
                Status: state.ApprovalStatus,
                OnApprove: new ButtonAction("gallery", "select", ["card:approval:approved"]),
                OnReject: new ButtonAction("gallery", "select", ["card:approval:rejected"]),
                SourceNeuronUri: selected.SourceNeuronUri),

            "app-install-card" => new RecordCardNode(
                "Smart App Manifest",
                "Tripletex Accounting Sync (v1.4.0)",
                [
                    new RecordFieldNode("Handle", "nordlys/tripletex-sync"),
                    new RecordFieldNode("Publisher", "Nordlys Digital (Verified)"),
                    new RecordFieldNode("Permissions", "Read Nordlys deals, Render interactive charts, Sync Tripletex ledger"),
                    new RecordFieldNode("Summary", "Synchronizes Norwegian Tripletex invoices with DigitalBrain Files & CRM."),
                ],
                Subtitle: "Verified Smart App package · 1-click capability review",
                LinkLabel: state.ApprovalStatus == "approved" ? "Installed" : "Review & Install",
                OnOpen: new ButtonAction("gallery", "select", ["card:approval:approved"])),

            "record-card" => new RecordCardNode(
                "Opportunity",
                "Nordlys Q4 Enterprise Renewal",
                [
                    new RecordFieldNode("Amount", "480,000 NOK"),
                    new RecordFieldNode("Close Date", "15 Nov 2026"),
                    new RecordFieldNode("Stage", "Negotiation / Review"),
                    new RecordFieldNode("Last Contact", "14 days ago"),
                ],
                Subtitle: "Kari Holm · Oslo, Norway",
                LinkLabel: "Open in Salesforce",
                OnOpen: new ButtonAction("gallery", "select", ["pulse"])),

            "secret-card" => new SecretCardNode(
                "Tripletex Session Token",
                "Required to verify Q4 invoice ledger balances in Tripletex. Stored encrypted in your personal vault.",
                SavedIn: "TRIPLETEX_SESSION_TOKEN",
                Status: state.SecretStatus,
                OnEnter: new ButtonAction("gallery", "select", ["card:secret:saved"]),
                OnSkip: new ButtonAction("gallery", "select", ["card:secret:pending"])),

            "job-card" => new JobCardNode(
                "Verifying BDD [Scenario] Gate & Deploying Smart App",
                [
                    new JobStepNode("Compile C# App<TState> grain", "done", "118ms"),
                    new JobStepNode("Execute [Scenario] BDD assertions", "done", "14/14 green"),
                    new JobStepNode("Publish neuron://local/apps/crm", state.JobStatus == "done" ? "done" : "running", "Streaming…"),
                ],
                Progress: state.JobStatus == "done" ? 1.0 : 0.75,
                Status: state.JobStatus,
                ResultText: "1.4s elapsed",
                OnCancel: new ButtonAction("gallery", "select", [state.JobStatus == "done" ? "card:job:running" : "card:job:done"])),

            "remember-card" => new RecordCardNode(
                "Episodic Memory",
                "Always format Nordlys renewal amounts in NOK with tabular figures.",
                [
                    new RecordFieldNode("Scope", "Nordlys AS · Importance 8/10"),
                ],
                Subtitle: "Nordlys AS",
                LinkLabel: state.ApprovalStatus == "approved" ? "Saved" : "Remember",
                OnOpen: new ButtonAction("gallery", "select", ["card:approval:approved"])),

            "file-cards" => new RecordCardNode(
                "Files Vault",
                "nordlys-q4-renewal.md",
                [
                    new RecordFieldNode("Type", "MD"),
                    new RecordFieldNode("Size", "18 KB"),
                    new RecordFieldNode("Path", "neuron://nordlys/storage.files/30-projects/nordlys-q4-renewal.md"),
                ],
                Subtitle: "30 Projects › Nordlys AS",
                LinkLabel: "Open in Files",
                OnOpen: new ButtonAction("apps/files", "open", [])),

            "error-line" => new InfoBar(
                "Salesforce didn't answer in time — nothing was changed yet",
                "Timeout after 10s on neuron://nordlys/salesforce.crm/q4-pipeline. Click Retry to run the request again.",
                Tone: "danger",
                Action: new Button("Retry Request", new ButtonAction("gallery", "select", ["pulse"]), Tone: ButtonTone.Accent) { Small = true, Icon = "refresh" }),

            "brain-scene-3d" => new BrainSceneNode(
                [
                    new BrainGraphNode("os", "OperatingSystem", "neuron://local/os", "core", 0, 0, 0, "working")
                    {
                        Subtitle = "Orleans Silo Kernel",
                        OnSelect = new ButtonAction("gallery", "select", ["node:os"]),
                    },
                    new BrainGraphNode("ino", "Ino Agent", "neuron://local/apps/ino", "agent", 120, 40, -20, "working")
                    {
                        Subtitle = "Personal AI Coordinator",
                        OnSelect = new ButtonAction("gallery", "select", ["node:ino"]),
                    },
                    new BrainGraphNode("pg", "Postgres", "neuron://local/postgres/main", "connector", -110, -50, 30, "done")
                    {
                        Subtitle = "Nordlys Analytics DB",
                        OnSelect = new ButtonAction("gallery", "select", ["node:pg"]),
                    },
                    new BrainGraphNode("files", "Files Vault", "neuron://local/storage.files", "storage", 85, -80, 60, "done")
                    {
                        Subtitle = "Hybrid Cloud + Local Disk",
                        OnSelect = new ButtonAction("gallery", "select", ["node:files"]),
                    },
                    new BrainGraphNode("creator", "Creator 3D", "neuron://local/apps/creator", "studio", -95, 75, -45, "working")
                    {
                        Subtitle = "Self-Improving Roslyn Studio",
                        OnSelect = new ButtonAction("gallery", "select", ["node:creator"]),
                    },
                ],
                [
                    new BrainGraphSynapse("os", "ino", "InoSend", true),
                    new BrainGraphSynapse("ino", "pg", "InoQueryDb", true),
                    new BrainGraphSynapse("ino", "files", "FileSavedSignal", true),
                    new BrainGraphSynapse("os", "creator", "CreatorInspect", true),
                ],
                SelectedId: state.SelectedBrainNode),

            "files-neuron-uri" or "neuron-paths" => new Column(
            [
                new NeuronRef("files", "neuron://owner/drive.files/projects/context.md"),
                new NeuronRef("table", "neuron://local/postgres/main#query-result"),
                new NeuronRef("view", "neuron://local/apps/gallery/view"),
            ])
            { Gap = 10 },

            _ => new RecordCardNode(
                selected.Category,
                selected.Title,
                [
                    new RecordFieldNode("Contract", selected.NeuronContract),
                    new RecordFieldNode("Neuron URI", selected.SourceNeuronUri),
                    new RecordFieldNode("Signals", string.Join(", ", selected.Signals)),
                ],
                Subtitle: selected.Summary),
        };
    }

    private static string BuildDynamicCodeSnippet(GalleryAppState state, GalleryCatalogEntry selected)
    {
        if (state.CodeTab == "flutter")
        {
            var primary = state.KnobStyle == "primary" ? "true" : "false";
            var danger = state.KnobStyle == "danger" ? "true" : "false";
            var small = state.KnobSmall ? "true" : "false";
            return $"// NeuronView -> _ImmutableView ({state.StylePreset} · {state.BarLayout})\n"
                + $"// Surface: {state.KnobSurface} · Tone: {state.KnobTone} · State: {state.KnobState}\n"
                + $"IcAcrylicBox(\n"
                + $"  padding: const EdgeInsets.all({(state.KnobSmall ? 14 : 20)}),\n"
                + $"  child: Column(\n"
                + $"    children: [\n"
                + $"      TagPill(label: '{selected.Signals.FirstOrDefault() ?? "gallery.select"}', tone: TagTone.{state.KnobTone}),\n"
                + $"      Btn(label: '{selected.Title}', primary: {primary}, danger: {danger}, small: {small}),\n"
                + $"    ],\n"
                + $"  ),\n"
                + $")";
        }

        if (state.CodeTab == "contract")
        {
            return $"Contract: {selected.NeuronContract}\n"
                + $"URI:      {selected.SourceNeuronUri}\n"
                + $"Signals:  [Signal] {string.Join(", ", selected.Signals)} (fired {state.SignalPulseCount} time(s))\n"
                + $"Identity: {state.StylePreset} ({state.BarLayout})\n"
                + $"Knobs:    style={state.KnobStyle}, tone={state.KnobTone}, small={state.KnobSmall}, state={state.KnobState}, surface={state.KnobSurface}";
        }

        return $"// {selected.NeuronContract} · [{string.Join(", ", selected.Signals)}]\n"
            + $"// Knobs: Style={state.KnobStyle} · Tone={state.KnobTone} · Size={(state.KnobSmall ? "Small(30px)" : "Standard(36px)")} · Surface={state.KnobSurface}\n"
            + selected.CSharpSample;
    }

    private static Button KnobButton(string label, string selectArg, bool active) => new(
        label,
        new ButtonAction("gallery", "select", [selectArg]),
        Tone: active ? ButtonTone.Accent : ButtonTone.Neutral)
    {
        Style = active ? "primary" : "secondary",
        Small = true,
    };

    private static BadgeTone MapKnobBadgeTone(string tone) => tone.ToLowerInvariant() switch
    {
        "done" or "ok" or "success" => BadgeTone.Done,
        "working" or "warn" or "warning" => BadgeTone.Working,
        "error" or "danger" => BadgeTone.Error,
        "neutral" => BadgeTone.Neutral,
        _ => BadgeTone.Accent,
    };
}
