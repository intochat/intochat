/*
# Gallery
Role: Default
Shipped: true
Operations: open, filter, select, switch-identity

WinUI 3 Gallery-inspired interactive UI Kit and Neuron contract catalog for IntoChat OS.
Displays live component previews, NodeContract schemas, [Signal] round-trips, neuron:// URIs,
and allows switching the active UI identity preset and bar layout.
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("gallery.open")]
public sealed record GalleryOpen(string? Category = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.filter")]
public sealed record GalleryFilter(string? Category = null, string? Search = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.select")]
public sealed record GallerySelect(string? ControlId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.switch-identity")]
public sealed record GallerySwitchIdentity(string? StylePreset = null, string? BarLayout = null) : DigitalBrain.Signal;

public sealed record GalleryCatalogEntry(
    string Id,
    string Category,
    string Title,
    string Summary,
    string NeuronContract,
    string[] Signals,
    string SourceNeuronUri,
    string CSharpSample);

public sealed record GalleryAppState(
    string Category,
    string Search,
    string SelectedId,
    string StylePreset,
    string BarLayout,
    string Status,
    GalleryCatalogEntry[] Entries)
{
    public GalleryAppState() : this(
        "all",
        "",
        "approval-card",
        "fluent-acrylic",
        "floating-glass",
        "Ready",
        DefaultCatalog)
    {
    }

    public static readonly GalleryCatalogEntry[] DefaultCatalog =
    [
        new(
            "acrylic-surface",
            "design",
            "AcrylicSurface & Specular Border",
            "Translucent dark obsidian glass with 24px backdrop blur and 1px top-lit specular border.",
            "IntoChat.UI.Surface@1",
            ["settings.save-preferences"],
            "neuron://local/ui.gallery/acrylic-surface",
            "Ui.AppShell(\"shell\", sidebar: Ui.NavItem(\"nav\", \"Overview\", \"layout-grid\", \"open\", selected: true), body: Ui.StatCard(\"kpi\", \"Blur\", \"28px\", \"+100%\", BadgeTone.Accent));"),
        new(
            "app-shell",
            "layout",
            "Ui.AppShell & Ui.StatCard Builders",
            "Ergonomic 30-line C# desktop layout builder with sidebar, toolbar, body, and split inspector.",
            "DigitalBrain.UI.AppShellNode@1",
            ["gallery.select"],
            "neuron://local/ui.gallery/app-shell",
            "Ui.AppShell(\"studio\", sidebar: Ui.NavItem(\"n1\", \"Studio\", \"sparkles\", \"open\", selected: true), body: Ui.StatCard(\"s1\", \"Throughput\", \"142 sig/m\", \"+18%\", BadgeTone.Success));"),
        new(
            "approval-card",
            "chat-lfp",
            "ApprovalCard (LFP)",
            "Inline human-in-the-loop confirmation card rendered inside Ino chat turns and Smart Desktop.",
            "IntoChat.UI.ApprovalCard@1",
            ["ino.approve-card"],
            "neuron://local/os.confirmations/inbox#approval-card",
            "await Send(new InoSend(\"Draft confirmation card\"));\nawait ApproveCard(new InoApproveCard(\"card-1\", Approved: true));"),
        new(
            "table-card",
            "chat-lfp",
            "TableCard & Postgres SQL Result (LFP)",
            "Interactive sortable data table rendered from IPostgresServer.Query or live neuron state.",
            "IntoChat.UI.TableCard@1",
            ["ino.connect-db", "ino.query-db"],
            "neuron://local/postgres/main#query-result",
            "await ConnectDb(new InoConnectDb(\"Host=localhost;Database=nordlys;Username=postgres\"));\nawait QueryDb(new InoQueryDb(\"SELECT account, stage, amount FROM opportunities\"));"),
        new(
            "chart-card",
            "chat-lfp",
            "ChartCard (LFP)",
            "Inline column and line chart card with comparison series and metric summary.",
            "IntoChat.UI.ChartCard@1",
            ["ino.send"],
            "neuron://local/ui.gallery/chart-card",
            "new Entry(\"chart-q4\", \"Won per month, 2026\", \"neuron://local/analytics/q4#chart\");"),
        new(
            "brain-scene-3d",
            "3d-brain",
            "BrainScene3D Orbital Graph",
            "Interactive 3D orbital projection of active neurons, modules, and traveling [Signal] photon pulses.",
            "IntoChat.UI.BrainScene@1",
            ["creator.inspect", "creator.prompt"],
            "neuron://local/creator/brain-scene#orbital",
            "await Inspect(new CreatorInspect(\"OperatingSystem\"));"),
        new(
            "files-neuron-uri",
            "controls",
            "Distributed neuron:// File Addressing",
            "Hybrid cloud + local file explorer exposing canonical neuron://{brain}/{connection}.files/{path} URIs.",
            "IntoChat.UI.Collection@1",
            ["files.location", "files.selected"],
            "neuron://owner/drive.files/projects/context.md",
            "var uri = filesApp.NeuronUriOf(selectedItem);"),
    ];
}

[DigitalBrain.App("gallery", Accent = "#22C55E", Category = "Studio")]
public sealed class GalleryApp : DigitalBrain.App<GalleryAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "gallery",
        "UI Gallery",
        "1.0.0",
        [],
        new DigitalBrain.AppRequirements([]))
    {
        Accent = "#22C55E",
        Category = "Studio",
    };

    public static AppBundle Bundle { get; } = new(
        "gallery",
        "1.0.0",
        new AppManifest(
            Title: "UI Gallery",
            Description: "WinUI 3-inspired UI Kit, NodeContract inspector, and UI Identity switcher.",
            Operations:
            [
                new("open", "Open the UI Kit gallery catalog."),
                new("filter", "Filter controls by category or search query."),
                new("select", "Inspect a control's NodeContract, [Signal]s, and neuron:// URI."),
                new("switch-identity", "Switch the active StylePreset and BarLayout."),
            ],
            Settings: [],
            Slots: [],
            Icon: "widgets",
            Toolbar:
            [
                new("open", Icon: "widgets", Label: "Catalog"),
                new("switch-identity", Icon: "palette", Label: "Switch Identity"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public async Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = string.IsNullOrWhiteSpace(request.Category) ? "all" : request.Category.Trim().ToLowerInvariant();
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetSettings() is { } settings)
        {
            var current = await settings.Read();
            preset = current.StylePreset;
            barLayout = current.BarLayout;
        }

        Set(State with
        {
            Category = category,
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Showing {FilteredEntries(category, State.Search).Length} UI Kit controls ({preset} · {barLayout})",
        });
    }

    [DigitalBrain.On]
    public void Filter(GalleryFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = string.IsNullOrWhiteSpace(request.Category) ? State.Category : request.Category.Trim().ToLowerInvariant();
        var search = request.Search?.Trim() ?? State.Search;
        var matches = FilteredEntries(category, search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;
        Set(State with
        {
            Category = category,
            Search = search,
            SelectedId = selected,
            Status = $"Filtered {matches.Length} control(s) in '{category}'",
        });
    }

    [DigitalBrain.On]
    public void Select(GallerySelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = string.IsNullOrWhiteSpace(request.ControlId) ? "approval-card" : request.ControlId.Trim();
        var match = State.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? State.Entries[0];
        Set(State with
        {
            SelectedId = match.Id,
            Status = $"Inspecting {match.Title} ({match.NeuronContract})",
        });
    }

    [DigitalBrain.On]
    public async Task SwitchIdentity(GallerySwitchIdentity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = string.IsNullOrWhiteSpace(request.StylePreset) ? State.StylePreset : request.StylePreset.Trim();
        var barLayout = string.IsNullOrWhiteSpace(request.BarLayout) ? State.BarLayout : request.BarLayout.Trim();
        if (TryGetSettings() is { } settings)
        {
            var saved = await settings.SetUiIdentity(preset, barLayout);
            preset = saved.StylePreset;
            barLayout = saved.BarLayout;
        }

        Set(State with
        {
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Active UI Identity: {preset} ({barLayout})",
        });
    }

    public View RenderSide()
    {
        var visible = FilteredEntries(State.Category, State.Search);
        var categoryButtons = new Node[]
        {
            Ui.NavItem("cat-all", "All", "layout-grid", "filter:all", selected: State.Category == "all", badge: $"{State.Entries.Length}"),
            Ui.NavItem("cat-design", "Design & Identity", "palette", "filter:design", selected: State.Category == "design"),
            Ui.NavItem("cat-controls", "Basic Input", "mouse-pointer-click", "filter:controls", selected: State.Category == "controls"),
            Ui.NavItem("cat-layout", "Layout & App Shell", "panel-left", "filter:layout", selected: State.Category == "layout"),
            Ui.NavItem("cat-lfp", "Data & 13 LFP Chat Cards", "table", "filter:chat-lfp", selected: State.Category == "chat-lfp"),
            Ui.NavItem("cat-3d", "3D Brain & Neurons", "brain-circuit", "filter:3d-brain", selected: State.Category == "3d-brain"),
        };

        return new(
            "UI Kit Catalog",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill(State.StylePreset, "accent", "palette"),
                            new TagPill(State.BarLayout, "neutral", "layout"),
                        ]) { Gap = 6 },
                        new Text($"Identity: {State.StylePreset} · {State.BarLayout}") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Active UI Identity"),
                new Column(categoryButtons) { Gap = 4 },
                new Collection(visible.Select(entry =>
                    new Entry(entry.Id, entry.Title, $"{entry.Category} · {entry.NeuronContract}")).ToArray()),
            ])
            { Gap = 12 });
    }

    public View Render()
    {
        var selected = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
        var livePreview = BuildSamplePreviewNode(selected);

        return new(
            "UI Gallery",
            new Column(
            [
                new Surface(
                    [
                        new Heading(selected.Title, Level: 1, Subtitle: selected.Summary),
                        new Row(
                        [
                            new TagPill(selected.Category, "accent"),
                            new TagPill(selected.NeuronContract, "ok", "cpu"),
                            new TagPill($"{State.StylePreset} · {State.BarLayout}", "neutral"),
                        ]) { Gap = 8 },
                        new Text(State.Status) { Tone = "muted" },
                        new Text($"Control: {selected.Title} [{selected.NeuronContract}]") { Tabular = true },
                        new Text($"URI: {selected.SourceNeuronUri}") { Mono = true, Tabular = true },
                        new Text($"Signals: {string.Join(", ", selected.Signals)}") { Mono = true },
                        new NeuronRef("gallery", selected.SourceNeuronUri),
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "WinUI 3 Gallery · Interactive Control Playground"),
                new Surface(
                    [
                        livePreview,
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Live Control Preview",
                    Subtitle: selected.NeuronContract),
                new Surface(
                    [
                        new Row(
                        [
                            new Button("Fluent Acrylic", new ButtonAction("gallery", "switch-identity", ["fluent-acrylic", "floating-glass"]), Tone: State.StylePreset == "fluent-acrylic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            new Button("Spatial Glass", new ButtonAction("gallery", "switch-identity", ["spatial-glass", "compact-pill"]), Tone: State.StylePreset == "spatial-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            new Button("Cyber Synaptic", new ButtonAction("gallery", "switch-identity", ["cyber-synaptic", "split-top-dock"]), Tone: State.StylePreset == "cyber-synaptic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            new Button("Warm Paper", new ButtonAction("gallery", "switch-identity", ["warm-paper", "classic-bottom"]), Tone: State.StylePreset == "warm-paper" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            new Button("Warm Mica", new ButtonAction("gallery", "switch-identity", ["fluent-warm-mica", "floating-glass"]), Tone: State.StylePreset == "fluent-warm-mica" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            new Button("Linear Carbon", new ButtonAction("gallery", "switch-identity", ["linear-carbon", "classic-bottom"]), Tone: State.StylePreset == "linear-carbon" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                            new Button("Nordic Tactical", new ButtonAction("gallery", "switch-identity", ["nordic-tactical", "split-top-dock"]), Tone: State.StylePreset == "nordic-tactical" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                        ]) { Gap = 8 },
                        new Text(selected.CSharpSample) { Mono = true },
                    ],
                    Kind: SurfaceKind.Recessed,
                    Title: "C# App<TState> & [Signal] Contract"),
            ])
            { Gap = 16 });
    }

    private static Node BuildSamplePreviewNode(GalleryCatalogEntry selected) => selected.Id switch
    {
        "app-shell" => Ui.AppShell(
            "gallery-shell-preview",
            sidebar: new Column(
            [
                Ui.NavItem("nav-1", "Overview", "layout-grid", "nav:overview", selected: true),
                Ui.NavItem("nav-2", "Signals", "radio", "nav:signals", badge: "14"),
            ]),
            body: Ui.StatCard("stat-1", "Active Signals / min", "142", "+18.4%", BadgeTone.Done, "zap"),
            toolbar: new Row([new Text("DigitalBrain.UI AppShell")])),
        "approval-card" => new ApprovalCardNode(
            "card-gallery-1",
            "Approve Opportunity Stage Change",
            "scope:neuron://local/apps/ino#card-1",
            [
                new ApprovalRowNode("Account", "Discovery", "Closed Won", "$420,000"),
                new ApprovalRowNode("Owner", "Unassigned", "Vlad Horb"),
            ],
            SystemName: "Ino",
            Status: "pending",
            OnApprove: new ButtonAction("ino", "approve-card", ["card-1", "true"]),
            OnReject: new ButtonAction("ino", "approve-card", ["card-1", "false"]),
            SourceNeuronUri: selected.SourceNeuronUri),
        "table-card" => new DataTable(
            "Opportunities — Q4 2026",
            ["Account", "Stage", "Amount", "Owner"],
            [
                ["Nordlys Bank", "Closed Won", "$420,000", "Vlad"],
                ["Solstice Bio", "Commit", "$185,000", "Aria"],
                ["Kestrel Aero", "Review", "$92,400", "Devon"],
            ],
            SourceNeuronUri: selected.SourceNeuronUri,
            From: "Postgres"),
        "chart-card" => new Chart(
            "bar",
            "Won per month, 2026",
            [
                new ChartPointNode("Jan", 68),
                new ChartPointNode("Feb", 82),
                new ChartPointNode("Mar", 94),
                new ChartPointNode("Apr", 128),
            ],
            ComparePoints: [54, 62, 71, 88],
            SeriesName: "2026",
            CompareName: "2025",
            SourceNeuronUri: selected.SourceNeuronUri,
            From: "Analytics",
            Prefix: "$"),
        "brain-scene-3d" => new BrainSceneNode(
            [
                new BrainGraphNode("os", "OperatingSystem", "neuron://local/os", "core", 0, 0, 0, "working"),
                new BrainGraphNode("ino", "Ino Agent", "neuron://local/apps/ino", "agent", 120, 40, -20, "working"),
                new BrainGraphNode("pg", "Postgres", "neuron://local/postgres/main", "connector", -110, -50, 30, "idle"),
            ],
            [
                new BrainGraphSynapse("os", "ino", "InoSend", true),
                new BrainGraphSynapse("ino", "pg", "InoQueryDb", true),
            ],
            SelectedId: "os"),
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

    [DigitalBrain.Scenario("Gallery filters controls, inspects NodeContract and neuron:// URI, and switches UI identity")]
    public async Task VerifyGalleryCatalogAndIdentitySwitchScenario()
    {
        await Open(new GalleryOpen());
        Filter(new GalleryFilter("chat-lfp", "Table"));
        if (State.SelectedId != "table-card")
        {
            throw new InvalidOperationException($"Expected 'table-card' to be selected after filtering, got '{State.SelectedId}'.");
        }

        await SwitchIdentity(new GallerySwitchIdentity("cyber-synaptic", "split-top-dock"));
        if (State.StylePreset != "cyber-synaptic" || State.BarLayout != "split-top-dock")
        {
            throw new InvalidOperationException("Expected GalleryApp to switch StylePreset and BarLayout.");
        }
    }

    private GalleryCatalogEntry[] FilteredEntries(string category, string search)
        => State.Entries
            .Where(e => category == "all" || string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(search)
                || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.NeuronContract.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private IUserSettings? TryGetSettings()
    {
        try
        {
            return Get<IUserSettings>("settings");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "UserSettingsNeuron is optional when running GalleryApp in isolation.");
            return null;
        }
    }
}
