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
            "new View(\"Surface\", new Column([new Text(\"Fluent Acrylic Glass\")]));"),
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

[DigitalBrain.App("gallery")]
public sealed class GalleryApp : DigitalBrain.App<GalleryAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "gallery",
        "UI Gallery",
        "1.0.0",
        [],
        new DigitalBrain.AppRequirements([]));

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
        return new(
            "UI Kit Catalog",
            new Column(
            [
                new Text($"Identity: {State.StylePreset} · {State.BarLayout}"),
                new Collection(visible.Select(entry =>
                    new Entry(entry.Id, entry.Title, $"{entry.Category} · {entry.NeuronContract}")).ToArray()),
            ]));
    }

    public View Render()
    {
        var selected = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
        return new(
            "UI Gallery",
            new Column(
            [
                new Text(State.Status),
                new Text($"Control: {selected.Title} [{selected.NeuronContract}]"),
                new Text($"URI: {selected.SourceNeuronUri}"),
                new Text($"Signals: {string.Join(", ", selected.Signals)}"),
                new Text(selected.Summary),
                new Text(selected.CSharpSample),
            ]));
    }

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
