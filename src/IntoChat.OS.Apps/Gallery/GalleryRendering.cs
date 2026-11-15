using System.Globalization;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
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

    private static readonly (string Span, string Status, int LatencyMs, string Module)[] TelemetrySpans =
    [
        ("ino.send", "Done", 12, "Apps"),
        ("synapse.check", "Done", 118, "Creator"),
        ("storage.stat", "Done", 4, "Storage"),
        ("ui.command", "Working", 19, "UI Kit"),
        ("vault.probe", "Done", 7, "Settings"),
    ];

    public static View Render(GalleryAppState state)
    {
        using var scope = CommandBinding.PushOwner("app.gallery");
        return new View("UI Gallery", BindOwner(RenderRoot(state, null), "app.gallery"));
    }

    public static View RenderSide(GalleryAppState state)
    {
        using var scope = CommandBinding.PushOwner("app.gallery");
        return new View("UI Kit Catalog", BindOwner(BuildSidebar(state), "app.gallery"));
    }

    public static Node RenderRoot(GalleryAppState state, Rejected? lastRejection)
    {
        var activeTab = GalleryAppState.NormalizeShowcaseTab(state.ShowcaseTab);

        return VStack(Space.Lg)[
            HeaderBar(
                "IntoChat UI Kit Gallery",
                "Warm Ink & Paper · 7 canonical component families · DDD Aggregate rule guards · Accordant state graphs",
                StatusPill(StatusKind.Done, $"{state.Entries.Length} components"),
                StatusPill(StatusKind.Idle, $"{state.StylePreset} · {state.BarLayout}")),
            Tabs(
                activeTab,
                Tab("foundation", "Foundation & status", Icons.Sparkles)
                    .Key("tab:foundation")
                    .OnSelect<GalleryAppState>(s => s.SelectTab("foundation")),
                Tab("inputs", "Inputs & rule guards", Icons.Landmark)
                    .Key("tab:inputs")
                    .OnSelect<GalleryAppState>(s => s.SelectTab("inputs")),
                Tab("data", "Data & charts", "chart-column")
                    .Key("tab:data")
                    .OnSelect<GalleryAppState>(s => s.SelectTab("data")),
                Tab("slots", "Live slots & Accordant state graph", "brain-circuit")
                    .Key("tab:slots")
                    .OnSelect<GalleryAppState>(s => s.SelectTab("slots")),
                Tab("catalog", "Component catalog", Icons.Sliders)
                    .Key("tab:catalog")
                    .OnSelect<GalleryAppState>(s => s.SelectTab("catalog"))),
            lastRejection is not null
                ? Banner(
                    "Domain rule refused command",
                    string.Join(" ", lastRejection.Messages),
                    tone: "error")
                : null,
            activeTab switch
            {
                "inputs" => RenderInputsAndGuardsTab(state),
                "data" => RenderDataAndChartsTab(state),
                "slots" => RenderSlotsAndStateGraphTab(state),
                "catalog" => RenderCatalogInspectorTab(state),
                _ => RenderFoundationAndStatusTab(state),
            }
        ];
    }

    private static Node RenderFoundationAndStatusTab(GalleryAppState state)
    {
        return VStack(Space.Lg)[
            ResponsiveSplit(
                leading: Surface(SurfaceKind.Elevated, "Typography & Hierarchy", "Family 1 · InterDisplay + Inter with tabular figures")[
                    VStack(Space.Sm)[
                        Heading("Warm ink on sunlit paper", 1, "Display scale pairs InterDisplay headings with legible Inter body copy."),
                        Heading("Section heading (level 2)", 2, "Sentence case across every bar, panel, and action."),
                        Heading("Card & panel heading (level 3)", 3),
                        Text("Body prose uses Inter 14/400 with generous line height so dense operational workspaces remain calm."),
                        Text("Tabular numerals keep live counters aligned: 1,492,048 tokens · 12.4 ms p95 · $50.00 cap.").Tabular().Strong(),
                        MutedText("Secondary captions use muted warm stone without falling back to developer monospace."),
                        Divider("Spacing & surfaces"),
                        HStack(Space.Sm)[
                            Text("4px grid"),
                            Spacer(Space.Sm),
                            MutedText($"Active status: {state.Status}").Tabular()
                        ]
                    ]
                ],
                trailing: VStack(Space.Md)[
                    Surface(SurfaceKind.Elevated, "Status Is Never Colour Alone", "Family 5 · All 5 DESIGN.md §2.5 semantic status triads")[
                        VStack(Space.Sm)[
                            MutedText("Every status badge pairs semantic foreground/tint tokens with a distinct Lucide icon and plain-language word:"),
                            HStack(Space.Sm)[
                                StatusPill(StatusKind.Done, "Done"),
                                StatusPill(StatusKind.Working, "Working"),
                                StatusPill(StatusKind.Error, "Error"),
                                StatusPill(StatusKind.Idle, "Idle"),
                                StatusPill(StatusKind.NeedsYou, "Needs your approval")
                            ],
                            Divider(),
                            Banner(
                                "Capability check passed",
                                "All 7 component families render through a single declarative C# View tree.",
                                tone: "info",
                                action: Button("Inspect inputs")
                                    .Key("foundation:go-inputs")
                                    .OnClick<GalleryAppState>(s => s.SelectTab("inputs")))
                        ]
                    ],
                    Card("Honest Empty State", "Family 5 · Clear guidance with one direct outcome button")[
                        Empty(
                            "No pinned snapshots yet",
                            "Open the interactive BankAccount aggregate or browse the 28-entry component catalog.",
                            PrimaryButton("Open rule guards")
                                .Key("empty:open-guards")
                                .OnClick<GalleryAppState>(s => s.SelectTab("inputs")))
                    ]
                ],
                leadingExtent: 460),
            Surface(SurfaceKind.Recessed, "UI Identity & Bar Layout Presets", "Family 2 · Instant theme materiality switching")[
                RenderIdentityPresetRow(state)
            ],
            Surface(SurfaceKind.Elevated, "WinUI-Grade Desktop Chrome & Media Surfaces (Kit v2)", "Toolbar, Breadcrumb, Segmented, SectionHeader, Expander, ImageView & CodeBlock")[
                VStack(Space.Md)[
                    Toolbar(
                        SectionHeader("Studio Chrome"),
                        Segmented(
                            state.SelectedOption,
                            Option("claude-sonnet-4-5", "Sonnet 4.5").Key("seg:claude").OnSelect<GalleryAppState>(s => s.SelectModelOption("claude-sonnet-4-5")),
                            Option("gpt-5-thinking", "GPT-5").Key("seg:gpt5").OnSelect<GalleryAppState>(s => s.SelectModelOption("gpt-5-thinking")),
                            Option("qwen3.5:4b", "Qwen Local").Key("seg:qwen").OnSelect<GalleryAppState>(s => s.SelectModelOption("qwen3.5:4b"))),
                        FlexSpacer(),
                        Breadcrumb(Text("Workspace"), Text("UI Kit v2"), StatusPill(StatusKind.Done, "Live"))),
                    Grid(2, Space.Md)[
                        ImageView(ArtCraftEngineRunner.GradientPreviewPngDataUrl, "Inline PNG Raster Canvas (ImageView)", height: 180),
                        CodeBlock("Declarative C# Kit v2", "HeaderBar(\"Studio\", \"Ready\",\n    PrimaryButton(\"Deploy\").Guard(state.CanDeploy));", "csharp", maxHeight: 180)
                    ],
                    Expander("Disclosure Panel (Expander)", "Click to inspect bounded progressive disclosure", expanded: false,
                        Text("Expanders keep secondary configuration tucked cleanly away without cluttering primary workflows."))
                ]
            ]
        ];
    }

    private static Node RenderInputsAndGuardsTab(GalleryAppState state)
    {
        var account = state.Account;
        var withdraw30Rule = account.SufficientFunds(30m);
        var withdraw70Rule = account.SufficientFunds(70m);

        return ResponsiveSplit(
            leading: Card(
                "Live BankAccount Aggregate",
                account.IsOpen
                    ? string.Create(CultureInfo.InvariantCulture, $"Holder: {account.Holder} · Shared Rule preconditions")
                    : "Closed account · Open to enable deposits and rule-guarded withdrawals")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        Metric(
                            "Account balance",
                            string.Create(CultureInfo.InvariantCulture, $"${account.Balance:0.00}"),
                            account.IsOpen ? "Open" : "Closed",
                            account.IsOpen ? BadgeTone.Done : BadgeTone.Neutral,
                            Icons.Landmark,
                            key: "metric:bank-balance"),
                        StatusPill(
                            account.IsOpen ? StatusKind.Done : StatusKind.Idle,
                            account.IsOpen ? $"Holder · {account.Holder}" : "Account closed")
                    ],
                    MutedText("Buttons bind directly to aggregate methods via expression trees. When SufficientFunds is broken, .Guard(rule) disables the button and attaches the exact domain refusal message as its hover tooltip."),
                    !account.IsOpen
                        ? HStack(Space.Sm)[
                            PrimaryButton("Open account for Alice", Icons.Plus)
                                .Key("bank:open-alice")
                                .OnClick<GalleryAppState>(s => s.OpenBankAccount("alice")),
                            Button("+$50", Icons.Plus)
                                .Key("bank:deposit-50-closed")
                                .OnClick<GalleryAppState>(s => s.DepositFunds(50m))
                                .Guard(account.MustBeOpen),
                            Button("-$30")
                                .Key("bank:withdraw-30-closed")
                                .OnClick<GalleryAppState>(s => s.WithdrawFunds(30m))
                                .Guard(withdraw30Rule),
                            DangerButton("-$70")
                                .Key("bank:withdraw-70-closed")
                                .OnClick<GalleryAppState>(s => s.WithdrawFunds(70m))
                                .Guard(withdraw70Rule)
                        ].ToNode()
                        : HStack(Space.Sm)[
                            PrimaryButton("+$50", Icons.Plus)
                                .Key("bank:deposit-50")
                                .OnClick<GalleryAppState>(s => s.DepositFunds(50m)),
                            Button("-$30")
                                .Key("bank:withdraw-30")
                                .OnClick<GalleryAppState>(s => s.WithdrawFunds(30m))
                                .Guard(withdraw30Rule),
                            DangerButton("-$70")
                                .Key("bank:withdraw-70")
                                .OnClick<GalleryAppState>(s => s.WithdrawFunds(70m))
                                .Guard(withdraw70Rule),
                            GhostButton("Reset account", Icons.RefreshCw)
                                .Key("bank:reset")
                                .OnClick<GalleryAppState>(s => s.ResetBankAccount())
                        ].ToNode(),
                    withdraw70Rule.Broken
                        ? MutedText($"Active guard tooltip on -$70: \"{withdraw70Rule.Message}\"").ToNode()
                        : Text("Both -$30 and -$70 withdrawals are currently satisfied by the account balance.").ToNode(),
                    GhostButton("View unfolded BankAccount state graph", "brain-circuit")
                        .Key("bank:view-graph")
                        .OnClick<GalleryAppState>(s => s.SelectTab("slots"))
                ]
            ],
            trailing: Surface(SurfaceKind.Elevated, "Inputs, Selectors & Policy Switches", "Family 3 · Typed Arg.Text bindings, SelectBox, SecretField & ToggleSwitch")[
                VStack(Space.Md)[
                    HStack(Space.Sm)[
                        PrimaryButton("Primary action", Icons.Sparkles)
                            .Key("ctrl:primary")
                            .Emits(AppSignals.Emit("gallery", "select", "pulse")),
                        Button("Secondary")
                            .Key("ctrl:secondary")
                            .Emits(AppSignals.Emit("gallery", "select", "pulse")),
                        GhostButton("Quiet ghost")
                            .Key("ctrl:ghost")
                            .Emits(AppSignals.Emit("gallery", "select", "pulse")),
                        DangerButton("Destructive", Icons.Trash2)
                            .Key("ctrl:danger")
                            .Emits(AppSignals.Emit("gallery", "select", "pulse")),
                        IconButton(Icons.RefreshCw, "Pulse signal")
                            .Key("ctrl:icon-pulse")
                            .Tooltip("Pulse live signal counter")
                            .Emits(AppSignals.Emit("gallery", "select", "pulse"))
                    ],
                    SearchField("Filter 28 gallery components…", "Component search")
                        .Key("ctrl:search-field")
                        .OnChange<GalleryAppState>(s => s.SetSearch(Arg.Text)),
                    TextField("Account holder", "Enter holder name (e.g. alice)…")
                        .Key("ctrl:holder-field")
                        .OnSubmit<GalleryAppState>(s => s.OpenBankAccount(Arg.Text)),
                    SecretField("API credential", "Paste token — write-only to vault")
                        .Key("ctrl:secret-field")
                        .Emits(AppSignals.Emit("gallery", "select", "card:secret:saved")),
                    SelectBox(
                        "Default reasoning model",
                        state.SelectedOption,
                        Option("claude-sonnet-4-5", "Claude Sonnet 4.5")
                            .Key("opt:claude-sonnet-4-5")
                            .OnSelect<GalleryAppState>(s => s.SelectModelOption("claude-sonnet-4-5")),
                        Option("gpt-5-thinking", "GPT-5 Thinking")
                            .Key("opt:gpt-5-thinking")
                            .OnSelect<GalleryAppState>(s => s.SelectModelOption("gpt-5-thinking")),
                        Option("qwen3.5:4b", "Qwen 3.5 4B (Local)")
                            .Key("opt:qwen-local")
                            .OnSelect<GalleryAppState>(s => s.SelectModelOption("qwen3.5:4b"))),
                    HStack(Space.Md)[
                        ToggleSwitch(
                            state.SwitchValue ? "Live signal stream active" : "Live signal stream paused",
                            state.SwitchValue,
                            locked: state.SwitchLocked)
                            .Key("ctrl:stream-switch")
                            .OnToggle<GalleryAppState>(s => s.ToggleLiveStream()),
                        Button(state.SwitchLocked ? "Unlock switch" : "Lock switch", "lock")
                            .Key("ctrl:lock-switch")
                            .Small()
                            .OnClick<GalleryAppState>(s => s.ToggleSwitchLock())
                    ]
                ]
            ],
            leadingExtent: 460);
    }

    private static Node RenderDataAndChartsTab(GalleryAppState state)
    {
        var orderedSpans = OrderSpans(state.TableSortColumn, state.TableSortDescending);
        var tableRows = orderedSpans
            .Select(row => (IReadOnlyList<string>)
            [
                row.Span,
                row.Status,
                string.Create(CultureInfo.InvariantCulture, $"{row.LatencyMs} ms"),
                row.Module,
            ])
            .ToArray();

        var primarySeries = new (string Label, double Value)[]
        {
            ("Jan", 68),
            ("Feb", 82),
            ("Mar", 94),
            ("Apr", 128 + state.SignalPulseCount),
        };
        var contextCompare = new double[] { 54, 62, 71, 88 };

        var lineChart = new Chart(
            "line",
            "Signal throughput (2026 vs 2025)",
            [.. primarySeries.Select(p => new ChartPointNode(p.Label, p.Value))],
            ComparePoints: contextCompare,
            SeriesName: "2026 (accent)",
            CompareName: "2025 (chartContext)",
            SourceNeuronUri: "neuron://local/ui.gallery/charts#line",
            From: "Telemetry");

        var barChart = new Chart(
            "columns",
            "Monthly requests by quarter window",
            [.. primarySeries.Select(p => new ChartPointNode(p.Label, p.Value))],
            ComparePoints: contextCompare,
            SeriesName: "Active (accent)",
            CompareName: "Baseline (chartContext)",
            SourceNeuronUri: "neuron://local/ui.gallery/charts#bar",
            From: "Telemetry");

        return VStack(Space.Lg)[
            Grid(3, Space.Md)[
                StatCard("kpi-throughput", "Active signals / min", $"{142 + state.SignalPulseCount}", "+18.4%", BadgeTone.Done, "zap"),
                StatCard("kpi-latency", "P95 grain turn latency", "6.8 ms", "-1.2 ms", BadgeTone.Accent, "activity"),
                StatCard("kpi-balance", "Sandbox account balance", string.Create(CultureInfo.InvariantCulture, $"${state.Account.Balance:0.00}"), state.Account.IsOpen ? "Open" : "Closed", BadgeTone.Neutral, Icons.Landmark)
            ],
            Grid(2, Space.Md)[
                Card("LineChart · Accent + chartContext", "Single Y-axis from zero · Zero rainbow hues")[
                    lineChart
                ],
                Card("BarChart · Paired comparison columns", "Primary series in accent, comparison in warm stone chartContext")[
                    barChart
                ]
            ],
            Surface(SurfaceKind.Elevated, "Sortable Telemetry Table", $"Family 6 · Sorted by {state.TableSortColumn} ({(state.TableSortDescending ? "desc" : "asc")})")[
                VStack(Space.Sm)[
                    HStack(Space.Sm)[
                        MutedText("Sort columns:"),
                        SortHeaderButton(state, "span", "Span"),
                        SortHeaderButton(state, "status", "Status"),
                        SortHeaderButton(state, "latency", "Duration"),
                        SortHeaderButton(state, "module", "Module")
                    ],
                    Table(
                        "Live Silo Spans",
                        ["Span", "Status", "Duration", "Module"],
                        tableRows,
                        sourceNeuronUri: "neuron://local/postgres/main#query-result")
                ]
            ]
        ];
    }

    private static Node SortHeaderButton(GalleryAppState state, string column, string label)
    {
        var active = string.Equals(state.TableSortColumn, column, StringComparison.Ordinal);
        var suffix = active ? (state.TableSortDescending ? " ↓" : " ↑") : "";
        return (active ? PrimaryButton(label + suffix) : GhostButton(label))
            .Key($"data:sort-{column}")
            .Small()
            .OnClick<GalleryAppState>(s => s.SortTableBy(column))
            .ToNode();
    }

    private static (string Span, string Status, int LatencyMs, string Module)[] OrderSpans(string sortColumn, bool descending)
    {
        IEnumerable<(string Span, string Status, int LatencyMs, string Module)> ordered = sortColumn switch
        {
            "span" => descending
                ? TelemetrySpans.OrderByDescending(x => x.Span, StringComparer.OrdinalIgnoreCase)
                : TelemetrySpans.OrderBy(x => x.Span, StringComparer.OrdinalIgnoreCase),
            "status" => descending
                ? TelemetrySpans.OrderByDescending(x => x.Status, StringComparer.OrdinalIgnoreCase)
                : TelemetrySpans.OrderBy(x => x.Status, StringComparer.OrdinalIgnoreCase),
            "module" => descending
                ? TelemetrySpans.OrderByDescending(x => x.Module, StringComparer.OrdinalIgnoreCase)
                : TelemetrySpans.OrderBy(x => x.Module, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? TelemetrySpans.OrderByDescending(x => x.LatencyMs)
                : TelemetrySpans.OrderBy(x => x.LatencyMs),
        };
        return [.. ordered];
    }

    private static Node RenderSlotsAndStateGraphTab(GalleryAppState state)
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
            ResponsiveSplit(
                leading: Surface(
                    SurfaceKind.Elevated,
                    "Accordant State Graph · BankAccount",
                    $"Unfolded live via AggregateExplorer<BankAccount> · {graph.States.Count} reachable states · {graph.AcceptedTransitions.Count} accepted edges · {graph.RejectedTransitions.Count} rule-guarded self-loops")[
                    VStack(Space.Md)[
                        HStack(Space.Sm)[
                            StatusPill(graph.IsHealthy ? StatusKind.Done : StatusKind.Error, graph.IsHealthy ? "0 invariant violations" : $"{graph.Violations.Count} violations"),
                            ToggleSwitch("Show rule-guarded self-loops", state.ShowRejectedSelfLoops)
                                .Key("graph:toggle-self-loops")
                                .OnToggle<GalleryAppState>(s => s.ToggleRejectedLoops())
                        ],
                        HStack(Space.Xs, stateButtons),
                        StateGraphView(graph, selectedState.Id, state.ShowRejectedSelfLoops),
                        MutedText($"Selected state {selectedState.Id} ({selectedState.Label}) at BFS depth {selectedState.Depth}.").Tabular()
                    ]
                ],
                trailing: VStack(Space.Md)[
                    Card("Live Sub-Neuron Slot Composition", "Family 2 · Law 5 micro-frontend embedding via Kit.Slot")[
                        VStack(Space.Sm)[
                            MutedText("Slot mounts any child neuron's live View independently without coupling parent generation state:"),
                            Slot("neuron://local/app.calculator/default"),
                            Slot("neuron://local/storage.files/work/context.md", "files")
                        ]
                    ],
                    Surface(SurfaceKind.Elevated, "Gate 2.0 Inline Chat Prompts", "Family 7 · ApprovalCard, ChoiceCard & SecretCard")[
                        VStack(Space.Md)[
                            new ApprovalCardNode(
                                "gallery-approval-1",
                                "plan-gallery-1",
                                "Approve write to /work/notes.md?",
                                "Storage · 1 file change gated by person-only confirmation",
                                [new ApprovalRowNode("Path", "/work/draft.md", "/work/notes.md")],
                                SystemName: "Files",
                                Status: state.ApprovalStatus,
                                SourceNeuronUri: "neuron://local/os.confirmations/inbox#gallery-1"),
                            new ChoiceCardNode(
                                "gallery-choice-1",
                                "Which workspace should receive the release digest?",
                                [
                                    new ChoiceOptionNode(
                                        "Primary workspace",
                                        "neuron://local/circles/primary",
                                        Button("Primary workspace")
                                            .Key("gallery:pick-0")
                                            .OnClick<GalleryAppState>(s => s.PickChoice(0))),
                                    new ChoiceOptionNode(
                                        "Engineering circle",
                                        "neuron://local/circles/eng",
                                        Button("Engineering circle")
                                            .Key("gallery:pick-1")
                                            .OnClick<GalleryAppState>(s => s.PickChoice(1))),
                                ],
                                PickedIndex: state.ChoicePickedIndex),
                            new SecretCardNode(
                                "GitHub API token",
                                "Required to read repository issues. Stored encrypted in your personal vault.",
                                SavedIn: "Secure Vault · GITHUB_TOKEN",
                                Status: state.SecretStatus,
                                Enter: PrimaryButton("Save to vault", Icons.KeyRound)
                                    .Key("gallery:secret-save")
                                    .OnClick<GalleryAppState>(s => s.SetSecretVaultStatus("saved")),
                                Skip: GhostButton("Skip")
                                    .Key("gallery:secret-skip")
                                    .OnClick<GalleryAppState>(s => s.SetSecretVaultStatus("pending")))
                        ]
                    ]
                ],
                leadingExtent: 540)
        ];
    }

    private static Node RenderCatalogInspectorTab(GalleryAppState state)
    {
        var selected = state.Entries.FirstOrDefault(e => e.Id == state.SelectedId) ?? state.Entries[0];
        var visible = GalleryLibrary.FilteredEntries(state.Entries, state.Category, state.Search);

        var body = string.Equals(state.ViewMode, "grid", StringComparison.OrdinalIgnoreCase)
            ? RenderCatalogGridSection(state, visible)
            : RenderCatalogDetailSection(state, selected);

        return ResponsiveSplit(
            leading: BuildSidebar(state),
            trailing: VStack(Space.Md)[
                HStack(Space.Sm)[
                    (state.ViewMode == "detail" ? PrimaryButton("Inspector view") : GhostButton("Inspector view"))
                        .Key("catalog:mode-detail")
                        .Small()
                        .OnClick<GalleryAppState>(s => s.SetCatalogViewMode("detail")),
                    (state.ViewMode == "grid" ? PrimaryButton("Grid view") : GhostButton("Grid view"))
                        .Key("catalog:mode-grid")
                        .Small()
                        .OnClick<GalleryAppState>(s => s.SetCatalogViewMode("grid")),
                    MutedText($"{visible.Length} of {state.Entries.Length} entries").Tabular()
                ],
                body
            ],
            leadingExtent: 268);
    }

    private static Node BuildSidebar(GalleryAppState state)
    {
        var visible = GalleryLibrary.FilteredEntries(state.Entries, state.Category, state.Search);
        (string Id, string Category, string Title, string Icon, int Count)[] categories =
        [
            ("cat-all", "all", "All controls", "layout-grid", state.Entries.Length),
            ("cat-design", "design", "Design & identity", "palette", state.Entries.Count(e => e.Category == "design")),
            ("cat-controls", "controls", "Basic input", "mouse-pointer-click", state.Entries.Count(e => e.Category == "controls")),
            ("cat-layout", "layout", "Layout & app shell", Icons.PanelLeft, state.Entries.Count(e => e.Category == "layout")),
            ("cat-lfp", "chat-lfp", "Data & chat cards", "table", state.Entries.Count(e => e.Category == "chat-lfp")),
            ("cat-3d", "3d-brain", "State & neural graphs", "brain-circuit", state.Entries.Count(e => e.Category == "3d-brain")),
        ];

        var categoryNodes = categories
            .Select(c => (Node)NavItem(
                c.Id,
                c.Title,
                c.Icon,
                $"filter:{c.Category}",
                selected: state.Category == c.Category,
                badge: $"{c.Count}",
                emit: AppSignals.Emit("gallery", "filter", c.Category, state.Search)))
            .ToArray();

        var entryNodes = visible
            .Select(entry => (Node)NavItem(
                $"sample-{entry.Id}",
                entry.Title,
                entry.Icon,
                $"select:{entry.Id}",
                selected: state.ViewMode != "grid" && string.Equals(state.SelectedId, entry.Id, StringComparison.OrdinalIgnoreCase),
                badge: GalleryLibrary.CategoryShortBadge(entry.Category),
                emit: AppSignals.Emit("gallery", "select", entry.Id)))
            .ToArray();

        return VStack(Space.Sm)[
            SearchField("Search categories & controls…", "Search catalog")
                .Key("gallery-search")
                .OnChange<GalleryAppState>(s => s.SetSearch(Arg.Text)),
            Divider("Categories"),
            VStack(Space.Xs, categoryNodes),
            Divider($"Controls ({visible.Length})"),
            VStack(Space.Xs, entryNodes)
        ];
    }

    private static Node RenderCatalogGridSection(GalleryAppState state, GalleryCatalogEntry[] visible)
    {
        if (visible.Length == 0)
        {
            return Empty(
                "No components match filter",
                $"Nothing matched '{state.Search}' in {GalleryLibrary.CategoryDisplayName(state.Category)}.",
                PrimaryButton("Reset filter")
                    .Key("catalog:reset-filter")
                    .Emits(AppSignals.Emit("gallery", "filter", "all", "")));
        }

        var cards = visible.Select(entry =>
        {
            var isSelected = string.Equals(entry.Id, state.SelectedId, StringComparison.OrdinalIgnoreCase);
            return Card(entry.Title, GalleryLibrary.CategoryDisplayName(entry.Category))[
                VStack(Space.Sm)[
                    HStack(Space.Xs)[
                        StatusPill(isSelected ? StatusKind.Done : StatusKind.Idle, GalleryLibrary.CategoryShortBadge(entry.Category)),
                        MutedText(entry.NeuronContract)
                    ],
                    MutedText(entry.Summary),
                    (isSelected ? PrimaryButton($"Inspect {entry.Title}", Icons.Check) : Button($"Inspect {entry.Title}", "arrow-up-right"))
                        .Key($"inspect-{entry.Id}")
                        .Small()
                        .OnClick<GalleryAppState>(s => s.InspectControl(entry.Id))
                ]
            ].ToNode();
        }).ToArray();

        return Grid(2, Space.Md, cards);
    }

    private static Node RenderCatalogDetailSection(GalleryAppState state, GalleryCatalogEntry selected)
    {
        var snippet = BuildDynamicCodeSnippet(state, selected);

        return VStack(Space.Md)[
            Surface(SurfaceKind.Elevated, selected.Title, selected.Summary)[
                VStack(Space.Sm)[
                    HStack(Space.Sm)[
                        StatusPill(StatusKind.Done, GalleryLibrary.CategoryDisplayName(selected.Category)),
                        StatusPill(StatusKind.Idle, selected.NeuronContract),
                        Button("Fire test signal", "zap")
                            .Key("catalog:pulse")
                            .Small()
                            .Emits(AppSignals.Emit("gallery", "select", "pulse")),
                        Button("Copy C# sample", "copy")
                            .Key("catalog:copy-sample")
                            .Small()
                            .WithIntent(IntentKind.Copy, snippet)
                    ],
                    MutedText($"URI: {selected.SourceNeuronUri} · Signals: {string.Join(", ", selected.Signals)} · Pulses: {state.SignalPulseCount}").Tabular(),
                    Slot(selected.SourceNeuronUri, "gallery")
                ]
            ],
            Card("Live Component Preview", $"{selected.NeuronContract} · {state.StylePreset}")[
                BuildSamplePreviewNode(state, selected)
            ],
            Surface(SurfaceKind.Recessed, "C# Reactor Sample", selected.NeuronContract)[
                Text(snippet).Tabular()
            ]
        ];
    }

    private static Node RenderIdentityPresetRow(GalleryAppState state)
    {
        (string Key, string Label, string Preset, string Layout, string Icon)[] presets =
        [
            ("preset-fluent-acrylic", "Fluent Acrylic", "fluent-acrylic", "floating-glass", "palette"),
            ("preset-spatial-glass", "Spatial Glass", "spatial-glass", "compact-pill", Icons.Sparkles),
            ("preset-cyber-synaptic", "Cyber Synaptic", "cyber-synaptic", "split-top-dock", "zap"),
            ("preset-warm-paper", "Warm Paper", "warm-paper", "classic-bottom", "sun"),
            ("preset-warm-mica", "Warm Mica", "fluent-warm-mica", "floating-glass", "sun"),
            ("preset-linear-carbon", "Linear Carbon", "linear-carbon", "classic-bottom", Icons.Sliders),
            ("preset-nordic-tactical", "Nordic Tactical", "nordic-tactical", "split-top-dock", "shield"),
        ];

        var buttons = presets.Select(p =>
            (state.StylePreset == p.Preset ? PrimaryButton(p.Label, p.Icon) : Button(p.Label, p.Icon))
                .Key(p.Key)
                .Small()
                .Emits(AppSignals.Emit("gallery", "switch-identity", p.Preset, p.Layout))
                .ToNode()).ToArray();

        return HStack(Space.Sm, buttons);
    }

    private static Node BuildSamplePreviewNode(GalleryAppState state, GalleryCatalogEntry selected)
    {
        return selected.Id switch
        {
            "themes" => VStack(Space.Sm)[
                Text($"Active identity: {state.StylePreset} ({state.BarLayout})").Strong().Tabular(),
                RenderIdentityPresetRow(state)
            ],
            "typography" => VStack(Space.Sm)[
                Heading("InterDisplay 30 · 1,492,048 tok", 1, "Inter 14 · Tabular numerals keep live telemetry aligned."),
                Text("99.98% uptime · 12.4 ms p95 · 142 signals/min").Tabular()
            ],
            "iconography" or "stat-card" => Grid(3, Space.Sm)[
                StatCard("sample-kpi-1", "Active signals / min", $"{142 + state.SignalPulseCount}", "+18.4%", BadgeTone.Done, "zap"),
                StatCard("sample-kpi-2", "Compute throughput", "842,100 tok", "+14.2%", BadgeTone.Accent, "cpu"),
                StatCard("sample-kpi-3", "P95 silo latency", "6.8 ms", "-1.2 ms", BadgeTone.Neutral, "activity")
            ],
            "tag-pill" => HStack(Space.Sm)[
                StatusPill(StatusKind.Done, "Done"),
                StatusPill(StatusKind.Working, "Working"),
                StatusPill(StatusKind.Error, "Error"),
                StatusPill(StatusKind.Idle, "Idle"),
                StatusPill(StatusKind.NeedsYou, "Needs you")
            ],
            "table-card" => Table(
                "Telemetry spans",
                ["Span", "Status", "Duration", "Module"],
                [
                    ["ino.send", "Done", "12 ms", "Apps"],
                    ["synapse.check", "Done", "118 ms", "Creator"],
                    ["storage.stat", "Done", "4 ms", "Storage"],
                ],
                sourceNeuronUri: selected.SourceNeuronUri),
            "chart-card" => BarChart(
                "Monthly throughput",
                [("Jan", 68), ("Feb", 82), ("Mar", 94), ("Apr", 128 + state.SignalPulseCount)],
                seriesName: "2026"),
            "brain-scene-3d" => StateGraphView(SharedBankGraph, state.SelectedGraphStateId, state.ShowRejectedSelfLoops),
            _ => VStack(Space.Sm)[
                MutedText(selected.Summary),
                StatusPill(StatusKind.Done, selected.NeuronContract)
            ],
        };
    }

    private static string BuildDynamicCodeSnippet(GalleryAppState state, GalleryCatalogEntry selected) => state.CodeTab switch
    {
        "contract" => $"Contract: {selected.NeuronContract} · URI: {selected.SourceNeuronUri} · Signals: {string.Join(", ", selected.Signals)}",
        _ => $"// {selected.NeuronContract} ({state.StylePreset})\n{selected.CSharpSample}",
    };
}
