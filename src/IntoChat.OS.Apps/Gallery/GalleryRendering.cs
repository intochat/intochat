using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Gallery;

internal static class GalleryRendering
{
    private static readonly Lazy<StateGraph<BankAccount>> UnfoldedBankGraph = new(() =>
    {
        var choices = new ChoiceSet()
            .ForParameter("holder", "alice")
            .ForParameter("amount", 50m, 30m, 70m);
        return AggregateExplorer<BankAccount>.Explore(choices: choices, maxStates: 16);
    });

    public static StateGraph<BankAccount> SharedBankGraph => UnfoldedBankGraph.Value;

    public static View Render(GalleryAppState state)
    {
        using var scope = CommandBinding.PushOwner("app.gallery");
        return new View("UI Gallery", BindOwner(RenderRoot(state, null), "app.gallery"));
    }

    public static Node RenderRoot(GalleryAppState state, Rejected? lastRejection)
    {
        var page = GalleryAppState.NormalizePage(state.Page);

        var toolbar = HeaderBar(
            "IntoChat UI Gallery",
            "WinUI 3-style live showcase · Stateful Modules.UI neurons over gRPC",
            StatusPill(StatusKind.Done, state.StylePreset),
            StatusPill(StatusKind.Idle, state.LastAction),
            Button("Open UI Kit", "widgets")
                .Key("gallery:open-uikit")
                .Small()
                .WithIntent(IntentKind.OpenWindow, "os://window/open?app=uikit"));

        var body = VStack(Space.Lg)[
            lastRejection is not null
                ? Banner("Domain rule refused command", string.Join(" ", lastRejection.Messages), tone: "error")
                : null,
            page switch
            {
                "colors" => RenderColorsPage(state),
                "typography" => RenderTypographyPage(),
                "icons" => RenderIconsPage(state),
                "spacing" => RenderSpacingPage(),
                "buttons" => RenderButtonsPage(state),
                "form" => RenderFormPage(),
                "winui-inputs" => RenderWinUiInputsPage(),
                "tabs" => RenderTabsPage(),
                "dialog" => RenderDialogPage(),
                "winui-nav" => RenderWinUiNavPage(),
                "media" => RenderMediaPage(),
                "workspace" => RenderWorkspaceKitPage(state),
                "wave3" => RenderWave3Page(),
                "charts" => RenderChartsPage(state),
                "table" => RenderTablePage(),
                "chat" => RenderChatPage(),
                "cards" => RenderCardsPage(),
                "graphs" => RenderGraphsPage(state),
                _ => RenderHomePage(state),
            }
        ];

        return new AppShellNode(
            Key: "gallery-shell",
            Sidebar: BuildNavigationRail(page),
            Body: body,
            Toolbar: toolbar);
    }

    private static Node BuildNavigationRail(string activePage)
    {
        Node NavButton(string id, string label, string icon) =>
            (activePage == id ? PrimaryButton(label, icon) : GhostButton(label, icon))
                .Key($"nav:{id}")
                .Small()
                .OnClick<GalleryAppState>(s => s.SelectPage(id))
                .ToNode();

        return VStack(Space.Xs)[
            NavButton("home", "Home", Icons.Sparkles),
            Divider("Design system"),
            NavButton("colors", "Colors & surfaces", "palette"),
            NavButton("typography", "Typography", "type"),
            NavButton("icons", "Iconography", "shapes"),
            NavButton("spacing", "Spacing & layout", "layout-grid"),
            Divider("Basic input"),
            NavButton("buttons", "Buttons & actions", "mouse-pointer-click"),
            NavButton("form", "Form & Vault (IForm)", "key-round"),
            NavButton("winui-inputs", "WinUI 3 Inputs (1)", "sliders"),
            Divider("Navigation & layout"),
            NavButton("tabs", "Tabs (ITabs)", "panel-top"),
            NavButton("dialog", "Dialog (IDialog)", "message-square"),
            NavButton("winui-nav", "WinUI 3 Nav (2)", "compass"),
            NavButton("media", "Menus, Grid & Media (3)", "image"),
            NavButton("workspace", "Workspace Kit (3)", "code"),
            NavButton("wave3", "WinUI 3 Wave 3 (3)", "milestone"),
            Divider("Data & charts"),
            NavButton("charts", "Charts (IChart)", "chart-column"),
            NavButton("table", "Data table (ITable)", "table"),
            Divider("Agent & patterns"),
            NavButton("chat", "DevUI Chat (IChatThread)", Icons.Sparkles),
            NavButton("cards", "Status & chat cards", "layers"),
            NavButton("graphs", "State graphs & DDD", "brain-circuit")
        ];
    }

    // -------------------------------------------------------------------------
    // HOME
    // -------------------------------------------------------------------------
    private static Node RenderHomePage(GalleryAppState state)
    {
        return VStack(Space.Lg)[
            Surface(
                SurfaceKind.Acrylic,
                "Welcome to IntoChat UI Gallery",
                "Every interactive control below is a real stateful DigitalBrain Neuron in IntoChat.OS.Modules.UI rendered directly over gRPC.")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        StatusPill(StatusKind.Done, "18 live UI neurons"),
                        StatusPill(StatusKind.Working, "gRPC NeuronWatchMux"),
                        StatusPill(StatusKind.Idle, "WinUI 3 NavigationShell")
                    ],
                    Divider("Live OS Identity & Theme Switcher"),
                    MutedText("Click any identity preset to persist to IUserSettings and live-update the entire Flutter OS shell:"),
                    RenderIdentityPresetRow(state)
                ]
            ],
            Heading("Explore by category", 2),
            Grid(3, Space.Md)[
                HomeTile("Charts (IChart)", "Two independent stateful IChart neurons: live streaming Append + IChartSource query binding.", "chart-column", "charts"),
                HomeTile("Data Table (ITable)", "Query-backed ITable neuron with live filter, sorting, pagination, and row selection.", "table", "table"),
                HomeTile("Basic Input", "Button tones, compact sizing, and guarded Buttons with domain invariants.", "mouse-pointer-click", "buttons"),
                HomeTile("Design System", "Colors, surfaces, InterDisplay + Inter typography, tabular numerals, icons, and 4pt/8pt spacing.", "palette", "colors"),
                HomeTile("Navigation & Layout", "ITabs, AppShell, SplitPane, and responsive grids.", "panel-top", "tabs"),
                HomeTile("State Graphs & DDD", "Live BankAccount aggregate with Rule guards and unfolded Accordant StateGraphView.", "brain-circuit", "graphs")
            ]
        ];
    }

    private static Node HomeTile(string title, string summary, string icon, string pageId) =>
        Card(title, summary)[
            PrimaryButton($"Open {title}", icon)
                .Key($"home:open:{pageId}")
                .Small()
                .OnClick<GalleryAppState>(s => s.SelectPage(pageId))
        ];

    private static Node RenderIdentityPresetRow(GalleryAppState state)
    {
        (string Key, string Label, string Preset, string Layout, string Icon)[] presets =
        [
            ("preset-warm-paper", "Warm Paper", "warm-paper", "classic-bottom", "sun"),
            ("preset-fluent-acrylic", "Fluent Acrylic", "fluent-acrylic", "floating-glass", "palette"),
            ("preset-warm-mica", "Fluent Warm Mica", "fluent-warm-mica", "floating-glass", "sun"),
            ("preset-linear-carbon", "Linear Carbon", "linear-carbon", "classic-bottom", Icons.Sliders),
            ("preset-spatial-glass", "Spatial Glass", "spatial-glass", "compact-pill", Icons.Sparkles),
            ("preset-cyber-synaptic", "Cyber Synaptic", "cyber-synaptic", "split-top-dock", "zap"),
            ("preset-nordic-tactical", "Nordic Tactical", "nordic-tactical", "split-top-dock", "shield")
        ];

        var buttons = presets.Select(p =>
            (state.StylePreset == p.Preset ? PrimaryButton(p.Label, p.Icon) : Button(p.Label, p.Icon))
                .Key(p.Key)
                .Small()
                .Sends(new Command(".", "gallery.switch-identity",
                    JsonSerializer.SerializeToElement(new GallerySwitchIdentity(p.Preset, p.Layout))))
                .ToNode()).ToArray();

        return HStack(Space.Sm, buttons);
    }

    // -------------------------------------------------------------------------
    // DESIGN SYSTEM
    // -------------------------------------------------------------------------
    private static Node RenderColorsPage(GalleryAppState state) => VStack(Space.Lg)[
        Heading("Colors & Surfaces", 1, "All 4 surface elevations adapt live when you switch the active OS identity."),
        RenderIdentityPresetRow(state),
        Grid(2, Space.Md)[
            Surface(SurfaceKind.Elevated, "Elevated Surface", "Primary cards, dialogs, and content containers.")[
                MutedText("Crisp 1px hairline border with subtle shadow depth.")
            ],
            Surface(SurfaceKind.Recessed, "Recessed Surface", "Code blocks, sidebars, and nested wells.")[
                MutedText("Lower visual elevation so primary cards pop forward.")
            ],
            Surface(SurfaceKind.Acrylic, "Acrylic / Glass Surface", "Hero banners, floating docks, and overlays.")[
                MutedText("Translucent frosted glass with specular edge highlight.")
            ],
            Surface(SurfaceKind.Subtle, "Subtle Surface", "Quiet grouping for secondary metadata.")[
                MutedText("Minimal chrome for dense operational views.")
            ]
        ],
        Surface(SurfaceKind.Elevated, "Semantic Status Palette", "Icon + Color rule: status is never communicated by a bare dot.")[
            HStack(Space.Sm)[
                StatusPill(StatusKind.Done, "Done / Healthy"),
                StatusPill(StatusKind.Working, "Working / Active"),
                StatusPill(StatusKind.NeedsYou, "Needs You / Warning"),
                StatusPill(StatusKind.Error, "Error / Refused"),
                StatusPill(StatusKind.Idle, "Idle / Neutral")
            ]
        ]
    ];

    private static Node RenderTypographyPage() => VStack(Space.Lg)[
        Heading("Typography", 1, "InterDisplay for headings, Inter for body & controls, tabular figures (tnum) for live telemetry."),
        Surface(SurfaceKind.Elevated, "Type Scale")[
            VStack(Space.Md)[
                Heading("Heading 1 · Page Title (InterDisplay)", 1),
                Heading("Heading 2 · Section Header (InterDisplay)", 2),
                Heading("Heading 3 · Card Title (InterDisplay)", 3),
                Text("Body copy uses Inter 14px with calm line height so dense workspaces remain readable all day."),
                Text("Tabular numerals (tnum): 1,492,048 tokens · 12.4 ms p95 · $50.00 / mo").Tabular().Strong(),
                MutedText("Muted caption · Secondary metadata, timestamps, and neuron addresses.")
            ]
        ],
        Surface(SurfaceKind.Elevated, "CodeBlock", "Monospace syntax-styled code display")[
            CodeBlock(
                "var chart = Get<IChart>(\"gallery/charts/stream\");\nawait chart.Append(new ChartPoint(\"T5\", 92, \"evt-5\"));",
                "csharp")
        ]
    ];

    private static Node RenderIconsPage(GalleryAppState state)
    {
        var categoryButtons = GalleryIconCatalog.Categories.Select(cat =>
        {
            var slug = cat.ToLowerInvariant().Replace('/', '-');
            return (string.Equals(state.IconCategory, cat, StringComparison.OrdinalIgnoreCase)
                    ? PrimaryButton(cat)
                    : GhostButton(cat))
                .Key($"icons:cat:{slug}")
                .Small()
                .Sends(SelectCmd($"icons:cat:{cat}"))
                .ToNode();
        }).ToArray();

        return VStack(Space.Lg)[
            Heading("Iconography Browser", 1, $"WinUI 3 icon browser · {GalleryIconCatalog.All.Length} categorized Lucide glyphs mapped in ui_icon.dart · Click any icon card to copy its slug."),
            Surface(SurfaceKind.Elevated, "Filter Icon System", $"Active category: {state.IconCategory}")[
                HStack(Space.Xs, categoryButtons)
            ],
            Card("Icon Grid (Click any glyph to copy slug)", $"Key: {GalleryApp.IconGridKey}")[
                UiNeuron("ui.grid-view", GalleryApp.IconGridKey)
            ],
            Surface(SurfaceKind.Elevated, "Common OS Semantic Pills")[
                HStack(Space.Sm)[
                    new TagPill("sparkles", "accent", Icons.Sparkles),
                    new TagPill("brain-circuit", "accent", "brain-circuit"),
                    new TagPill("kanban", "done", "kanban"),
                    new TagPill("milestone", "working", "milestone"),
                    new TagPill("braces", "neutral", "braces"),
                    new TagPill("cpu", "accent", "cpu"),
                    new TagPill("shield-check", "done", "shield-check")
                ]
            ]
        ];
    }

    private static Node RenderSpacingPage() => VStack(Space.Lg)[
        Heading("Spacing, Padding & Layout", 1, "Strict 4pt/8pt spacing scale and responsive multi-column grids."),
        Grid(4, Space.Md)[
            Card("Space.Xs (4px)", "Tight icon/label pairs")[MutedText("Compact inline gaps")],
            Card("Space.Sm (8px)", "Button rows & list items")[MutedText("Standard control rhythm")],
            Card("Space.Md (12-16px)", "Cards & section groups")[MutedText("Default card padding")],
            Card("Space.Lg (20-24px)", "Major page sections")[MutedText("Breathing room")]
        ]
    ];

    // -------------------------------------------------------------------------
    // BASIC INPUT
    // -------------------------------------------------------------------------
    private static Node RenderButtonsPage(GalleryAppState state) => VStack(Space.Lg)[
        Heading("Buttons & Actions", 1, "All button tones, compact sizing, icon buttons, and domain-guarded disabled tooltips."),
        Card("Button Variants", $"Recorded clicks: {state.ButtonClicks}")[
            VStack(Space.Md)[
                HStack(Space.Sm)[
                    PrimaryButton("Primary", Icons.Sparkles)
                        .Key("btn:primary")
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("Primary")),
                    Button("Secondary", "check")
                        .Key("btn:secondary")
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("Secondary")),
                    GhostButton("Ghost", "arrow-up-right")
                        .Key("btn:ghost")
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("Ghost")),
                    DangerButton("Danger", "trash")
                        .Key("btn:danger")
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("Danger")),
                    IconButton("zap", "Fire pulse")
                        .Key("btn:icon")
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("IconButton"))
                ],
                HStack(Space.Sm)[
                    PrimaryButton("Compact Primary", Icons.Check)
                        .Key("btn:compact")
                        .Small()
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("Compact Primary")),
                    Button("Guarded Disabled Button", "lock")
                        .Key("btn:guarded")
                        .Guard(Decision.Rule("demo_guard", true, static _ => true, "Disabled by domain Rule guard."))
                        .OnClick<GalleryAppState>(s => s.RecordButtonClick("Guarded"))
                ]
            ]
        ]
    ];

    // -------------------------------------------------------------------------
    // NAVIGATION & LAYOUT (LIVE NEURONS)
    // -------------------------------------------------------------------------
    private static Node RenderTabsPage() => VStack(Space.Lg)[
        ControlHeader("Tabs", "IntoChat.OS.Modules.UI.Tabs.ITabs", GalleryApp.DemoTabsKey,
            "Stateful tab strip neuron emitting TabChanged."),
        Card("Live ITabs Neuron", $"Key: {GalleryApp.DemoTabsKey}")[
            UiNeuron("ui.tabs", GalleryApp.DemoTabsKey)
        ],
        CodeSample(
            "var tabs = Get<ITabs>(\"gallery/tabs/demo\");\n" +
            "await tabs.Configure([new UiTabItem(\"overview\", \"Overview\"), new UiTabItem(\"signals\", \"Signals\")]);")
    ];

    // -------------------------------------------------------------------------
    // DATA & CHARTS (2 INDEPENDENT LIVE IChart NEURONS + ITable)
    // -------------------------------------------------------------------------
    private static Node RenderChartsPage(GalleryAppState state) => VStack(Space.Lg)[
        ControlHeader("Charts (Unified Stateful IChart)", "IntoChat.OS.Modules.UI.Chart.IChart",
            $"{GalleryApp.StreamChartKey} & {GalleryApp.RevenueChartKey}",
            "Two independent IChart neurons side-by-side: each owns its own Kind, Series, Query, Docs, and SelectedPoint."),
        Grid(2, Space.Md)[
            Surface(SurfaceKind.Elevated, "1. Streaming IChart Neuron", "200-point rolling window · EventId dedup · Live kind switching")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        PrimaryButton("Append point", "plus")
                            .Key("chart:stream-append")
                            .Small()
                            .Sends(SelectCmd("stream:append")),
                        StatusPill(StatusKind.Working, $"Points appended: {state.StreamPointCounter}")
                    ],
                    UiNeuron("ui.chart", GalleryApp.StreamChartKey)
                ]
            ],
            Surface(SurfaceKind.Elevated, "2. Query-Bound IChart Neuron", $"Backed by SampleChartSource (IChartSource) · Active filter: {state.RevenueQueryFilter}")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        (state.RevenueQueryFilter == "all" ? PrimaryButton("All (H1)") : GhostButton("All (H1)"))
                            .Key("chart:filter-all")
                            .Small()
                            .Sends(SelectCmd("revenue:filter:all")),
                        (state.RevenueQueryFilter == "q1" ? PrimaryButton("Q1 only") : GhostButton("Q1 only"))
                            .Key("chart:filter-q1")
                            .Small()
                            .Sends(SelectCmd("revenue:filter:q1")),
                        (state.RevenueQueryFilter == "q2" ? PrimaryButton("Q2 only") : GhostButton("Q2 only"))
                            .Key("chart:filter-q2")
                            .Small()
                            .Sends(SelectCmd("revenue:filter:q2"))
                    ],
                    UiNeuron("ui.chart", GalleryApp.RevenueChartKey)
                ]
            ]
        ],
        CodeSample(
            "// 1. Streaming chart neuron (independent state & rolling 200-point window)\n" +
            "var stream = Get<IChart>(\"gallery/charts/stream\");\n" +
            "await stream.Append(new ChartPoint(\"T5\", 92, \"evt-5\"));\n\n" +
            "// 2. Query-bound chart neuron (live IChartSource + docs + kind switch + point selection)\n" +
            "var revenue = Get<IChart>(\"gallery/charts/revenue\");\n" +
            "await revenue.Bind(\"owner/ui.sample-chart-source/default\", new ChartQuery(Filter: \"q1\"));\n" +
            "await revenue.SetKind(ChartKinds.Area);")
    ];

    private static Node RenderTablePage() => VStack(Space.Lg)[
        ControlHeader("Data Table", "IntoChat.OS.Modules.UI.Table.ITable", GalleryApp.SpansTableKey,
            "Query-backed ITable neuron bound to SampleTableSource: live filter, column sort, pagination, and row selection."),
        UiNeuron("ui.table", GalleryApp.SpansTableKey),
        CodeSample(
            "var table = Get<ITable>(\"gallery/table/spans\");\n" +
            "await table.Bind(\"owner/ui.sample-table-source/default\", \"Live Silo Spans\", columns, pageSize: 5);\n" +
            "await table.Filter(\"Modules.UI\");\n" +
            "await table.Sort(\"latency\");")
    ];

    private static Node RenderFormPage() => VStack(Space.Lg)[
        ControlHeader("Form & Vault Secrets", "IntoChat.OS.Modules.UI.Form.IForm", GalleryApp.DemoFormKey,
            "Refuses plaintext on Secret fields; stores opaque Vault SecretRef handles instead."),
        UiNeuron("ui.form", GalleryApp.DemoFormKey),
        CodeSample(
            "var form = Get<IForm>(\"gallery/form/demo\");\n" +
            "await form.SetSecretRef(\"apiKey\", new UiSecretRef(\"vault://ANTHROPIC_API_KEY\"));\n" +
            "await form.Submit();")
    ];

    private static Node RenderDialogPage() => VStack(Space.Lg)[
        ControlHeader("Dialog", "IntoChat.OS.Modules.UI.Dialog.IDialog", GalleryApp.DemoDialogKey,
            "Confirm and prompt-with-input modal neuron emitting DialogResolved."),
        HStack(Space.Sm)[
            PrimaryButton("Open live IDialog", "message-square")
                .Key("dialog:open")
                .Small()
                .Sends(SelectCmd("dialog:open"))
        ],
        UiNeuron("ui.dialog", GalleryApp.DemoDialogKey),
        CodeSample(
            "var dlg = Get<IDialog>(\"gallery/dialog/demo\");\n" +
            "await dlg.Open(\"Create folder\", \"Enter folder name\", \"Create\", \"Cancel\", inputLabel: \"Folder name\");")
    ];

    private static Node RenderWinUiInputsPage() => VStack(Space.Lg)[
        Heading("WinUI 3 Basic Input Neurons", 1, "ISlider — a live stateful neuron in Modules.UI."),
        Card("ISlider", GalleryApp.DemoSliderKey)[UiNeuron("ui.slider", GalleryApp.DemoSliderKey)]
    ];

    private static Node RenderWinUiNavPage() => VStack(Space.Lg)[
        Heading("WinUI 3 Navigation & Collections Neurons", 1, "IBreadcrumbBar and ITreeView."),
        Grid(2, Space.Md)[
            Card("IBreadcrumbBar", GalleryApp.DemoBreadcrumbKey)[UiNeuron("ui.breadcrumb", GalleryApp.DemoBreadcrumbKey)],
            Card("ITreeView", GalleryApp.DemoTreeViewKey)[UiNeuron("ui.tree-view", GalleryApp.DemoTreeViewKey)]
        ]
    ];

    private static Node RenderChatPage() => VStack(Space.Lg)[
        ControlHeader("DevUI Chat Workbench", "IntoChat.OS.Modules.UI.Chat.IChatThread", GalleryApp.DevUiChatKey,
            "Collapsible AiReasoning, expandable tool-call JSON cards, live inline IChart slots, Context Inspector, /compact, and model picker."),
        UiNeuron("ui.chat", GalleryApp.DevUiChatKey),
        CodeSample(
            "var chat = Get<IChatThread>(\"gallery/chat/devui\");\n" +
            "await chat.StreamDelta(\"a1\", textDelta: \"Throughput grew +66%\", reasoningDelta: \"Query SampleChartSource\");\n" +
            "await chat.UpsertToolCall(\"a1\", new ChatToolCall(\"tc-1\", \"ui.chart.bind\", args, result, \"done\", 14));")
    ];

    // -------------------------------------------------------------------------
    // STATUS & CHAT CARDS
    // -------------------------------------------------------------------------
    private static Node RenderCardsPage() => VStack(Space.Lg)[
        Heading("Status & Chat Cards", 1, "KPI stat cards, progress indicators, banners, and inline LFP agent cards."),
        Grid(3, Space.Md)[
            StatCard("kpi-signals", "Active signals / min", "148", "+18.4%", BadgeTone.Done, "zap"),
            StatCard("kpi-latency", "P95 gRPC watch latency", "4.2 ms", "-1.8 ms", BadgeTone.Accent, "activity"),
            StatCard("kpi-neurons", "UI Kit neurons", "18", "Modules.UI", BadgeTone.Neutral, "layers")
        ],
        Surface(SurfaceKind.Elevated, "Progress & Banners")[
            VStack(Space.Sm)[
                new Progress("Indexing brain memory", 0.72, "72%"),
                Banner("Unified DDD UI Kit active", "Interactive controls talk directly to Modules.UI neurons over gRPC.", tone: "info")
            ]
        ],
        Grid(2, Space.Md)[
            new RecordCardNode(
                "Customer · Acme Corp",
                "Synced from Postgres",
                [new RecordFieldNode("Plan", "Enterprise"), new RecordFieldNode("Seats", "42"), new RecordFieldNode("Status", "Active")]),
            new JobCardNode(
                "Nightly Memory Consolidation",
                [new JobStepNode("Consolidate episodes", "done"), new JobStepNode("Rebuild vector index", "running")],
                0.72)
        ]
    ];

    // -------------------------------------------------------------------------
    // PATTERNS & GRAPHS (BANK ACCOUNT DDD + STATE GRAPH)
    // -------------------------------------------------------------------------
    private static Node RenderGraphsPage(GalleryAppState state)
    {
        var graph = SharedBankGraph;
        var selectedState = graph.States.FirstOrDefault(s => s.Id == state.SelectedGraphStateId) ?? graph.States[0];

        var stateButtons = graph.States
            .Select(node => (node.Id == selectedState.Id
                    ? PrimaryButton($"{node.Id} · {node.Label}")
                    : GhostButton($"{node.Id} · {node.Label}"))
                .Key($"graph:state-{node.Id}")
                .Small()
                .OnClick<GalleryAppState>(s => s.SelectGraphState(node.Id))
                .ToNode())
            .ToArray();

        return VStack(Space.Lg)[
            Heading("State Graphs & DDD Rule Guards", 1, "Live BankAccount aggregate with declarative Rule guards and unfolded StateGraphView."),
            Surface(SurfaceKind.Elevated, $"BankAccount Aggregate · {state.Account}",
                "Hover disabled buttons to see the exact domain Rule refusal reason.")[
                HStack(Space.Sm)[
                    PrimaryButton("Open for Alice", "user-plus")
                        .Key("bank:open")
                        .Small()
                        .Guard(state.Account.MustNotBeOpen)
                        .OnClick<GalleryAppState>(s => s.OpenBankAccount("Alice")),
                    Button("Deposit $50", "plus")
                        .Key("bank:deposit-50")
                        .Small()
                        .Guard(state.Account.MustBeOpen)
                        .OnClick<GalleryAppState>(s => s.DepositFunds(50m)),
                    Button("Withdraw $30", "minus")
                        .Key("bank:withdraw-30")
                        .Small()
                        .Guard(state.Account.SufficientFunds(30m))
                        .OnClick<GalleryAppState>(s => s.WithdrawFunds(30m)),
                    DangerButton("Withdraw $70", "minus")
                        .Key("bank:withdraw-70")
                        .Small()
                        .Guard(state.Account.SufficientFunds(70m))
                        .OnClick<GalleryAppState>(s => s.WithdrawFunds(70m))
                ]
            ],
            Surface(SurfaceKind.Elevated, "Accordant State Graph",
                $"{graph.States.Count} reachable states · {graph.AcceptedTransitions.Count} accepted edges · {graph.RejectedTransitions.Count} rule-guarded self-loops")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        StatusPill(graph.IsHealthy ? StatusKind.Done : StatusKind.Error,
                            graph.IsHealthy ? "0 invariant violations" : $"{graph.Violations.Count} violations"),
                        Button(state.ShowRejectedSelfLoops ? "Hide self-loops" : "Show self-loops", "git-branch")
                            .Key("graph:toggle-loops")
                            .Small()
                            .OnClick<GalleryAppState>(s => s.ToggleRejectedLoops())
                    ],
                    HStack(Space.Xs, stateButtons),
                    StateGraphView(graph, selectedState.Id, state.ShowRejectedSelfLoops)
                ]
            ]
        ];
    }

    private static Node RenderMediaPage() => VStack(Space.Lg)[
        Heading("Menus, Grid & Media Controls", 1, "WinUI 3 ContextMenu, GridView, and ImageViewer neurons."),
        Card("ContextMenu (IContextMenu)", $"Key: {GalleryApp.DemoContextMenuKey}")[
            UiNeuron("ui.context-menu", GalleryApp.DemoContextMenuKey)
        ],
        Card("GridView (IGridView)", $"Key: {GalleryApp.DemoGridViewKey}")[
            UiNeuron("ui.grid-view", GalleryApp.DemoGridViewKey)
        ],
        Card("ImageViewer (IImageViewer)", $"Key: {GalleryApp.DemoImageViewerKey}")[
            UiNeuron("ui.image-viewer", GalleryApp.DemoImageViewerKey)
        ]
    ];

    private static Node RenderWorkspaceKitPage(GalleryAppState state)
    {
        string[] langs = ["csharp", "dart", "json", "sql", "typescript", "yaml", "markdown"];
        var langButtons = langs.Select(lang =>
            (string.Equals(state.CodeLanguage, lang, StringComparison.OrdinalIgnoreCase)
                ? PrimaryButton(lang.ToUpperInvariant())
                : GhostButton(lang.ToUpperInvariant()))
                .Key($"code:lang:{lang}")
                .Small()
                .Sends(SelectCmd($"code:lang:{lang}"))
                .ToNode()).ToArray();

        return VStack(Space.Lg)[
            Heading("Workspace Kit (Wave 2 + Syntax Highlighting)", 1, "CommandBar, re_highlight CodeEditor (7 languages), and MarkdownViewer neurons."),
            Card("CommandBar (ICommandBar)", $"Key: {GalleryApp.DemoCommandBarKey}")[
                UiNeuron("ui.command-bar", GalleryApp.DemoCommandBarKey)
            ],
            Card("Syntax-Highlighted CodeEditor (ICodeEditor · re_highlight)", $"Key: {GalleryApp.DemoCodeEditorKey} · Active grammar: {state.CodeLanguage}")[
                VStack(Space.Sm)[
                    HStack(Space.Xs, langButtons),
                    UiNeuron("ui.code-editor", GalleryApp.DemoCodeEditorKey)
                ]
            ],
            Card("MarkdownViewer (IMarkdownViewer)", $"Key: {GalleryApp.DemoMarkdownKey}")[
                UiNeuron("ui.markdown", GalleryApp.DemoMarkdownKey)
            ]
        ];
    }

    private static Node RenderWave3Page() => VStack(Space.Lg)[
        Heading("WinUI 3 Parity Kit (Wave 3)", 1, "Stepper, PropertyGrid, and KanbanBoard neurons."),
        Card("Stepper (IStepper)", $"Key: {GalleryApp.DemoStepperKey}")[
            UiNeuron("ui.stepper", GalleryApp.DemoStepperKey)
        ],
        Card("KanbanBoard (IKanbanBoard)", $"Key: {GalleryApp.DemoKanbanKey}")[
            UiNeuron("ui.kanban", GalleryApp.DemoKanbanKey)
        ],
        Card("PropertyGrid (IPropertyGrid)", $"Key: {GalleryApp.DemoPropertyGridKey}")[
            UiNeuron("ui.property-grid", GalleryApp.DemoPropertyGridKey)
        ]
    ];

    // -------------------------------------------------------------------------
    // HELPERS
    // -------------------------------------------------------------------------
    private static Node ControlHeader(string title, string contract, string key, string subtitle) =>
        VStack(Space.Xs)[
            Heading(title, 1, subtitle),
            HStack(Space.Sm)[
                StatusPill(StatusKind.Done, contract),
                StatusPill(StatusKind.Idle, $"neuron://{key}")
            ]
        ];

    private static Node CodeSample(string code) =>
        Surface(SurfaceKind.Recessed, "C# Contract & Usage", "IntoChat.OS.Modules.UI")[
            CodeBlock(code, "csharp")
        ];

    private static Node UiNeuron(string kind, string neuronKey) =>
        new NeuronRef(kind, neuronKey);

    private static Command SelectCmd(string controlId) =>
        new(".", "gallery.select", JsonSerializer.SerializeToElement(new GallerySelect(controlId)));
}
