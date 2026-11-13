/*
# Gallery
Role: Default
Shipped: true
Operations: open, filter, select, switch-identity

WinUI 3 Gallery-inspired interactive UI Kit and Neuron contract catalog for IntoChat OS.
Displays live component previews, NodeContract schemas, [Signal] round-trips, neuron:// URIs,
interactive Property Knobs, dynamic C# / Flutter / Contract code inspection, and allows switching
the active UI identity preset and bar layout.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Apps.Gallery;

[Signal("gallery.open")]
public sealed record GalleryOpen(string? Category = null) : Signal;

[Signal("gallery.filter")]
public sealed record GalleryFilter(string? Category = null, string? Search = null) : Signal;

[Signal("gallery.select")]
public sealed record GallerySelect(string? ControlId = null) : Signal;

[Signal("gallery.switch-identity")]
public sealed record GallerySwitchIdentity(string? StylePreset = null, string? BarLayout = null) : Signal;

public sealed record GalleryAppState(
    string Category,
    string Search,
    string SelectedId,
    string StylePreset,
    string BarLayout,
    string Status,
    GalleryCatalogEntry[] Entries)
{
    public string ViewMode { get; init; } = "detail";
    public string KnobStyle { get; init; } = "primary";
    public string KnobTone { get; init; } = "accent";
    public bool KnobSmall { get; init; }
    public string KnobState { get; init; } = "interactive";
    public string KnobSurface { get; init; } = "acrylic";
    public string KnobChartKind { get; init; } = "bar";
    public string CodeTab { get; init; } = "csharp";
    public int SignalPulseCount { get; init; }
    public bool SwitchValue { get; init; } = true;
    public bool SwitchLocked { get; init; }
    public string SelectedOption { get; init; } = "claude-sonnet-4-5";
    public string ShellNav { get; init; } = "overview";
    public string ApprovalStatus { get; init; } = "pending";
    public int? ChoicePickedIndex { get; init; }
    public string DraftStatus { get; init; } = "pending";
    public string SecretStatus { get; init; } = "pending";
    public string JobStatus { get; init; } = "running";
    public string SelectedBrainNode { get; init; } = "os";

    public GalleryAppState() : this(
        "all",
        "",
        "approval-card",
        "fluent-acrylic",
        "floating-glass",
        "Ready",
        GalleryLibrary.DefaultCatalog)
    {
    }
}

[Neuron("app.gallery")]
public sealed class GalleryApp : Screen<GalleryAppState>
{
    public const string AppName = "gallery";

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
            Dock: 8,
            SideWidth: 268d,
            Toolbar:
            [
                new("open", Icon: "widgets", Label: "Catalog"),
                new("switch-identity", Icon: "palette", Label: "Switch Identity"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(GalleryApp).AssemblyQualifiedName,
    };

    [On]
    public async Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
        {
            var userPref = await settings.Read();
            preset = userPref.StylePreset;
            barLayout = userPref.BarLayout;
        }

        var category = string.IsNullOrWhiteSpace(request.Category) ? State.Category : GalleryLibrary.NormalizeCategory(request.Category);
        var matches = GalleryLibrary.FilteredEntries(State.Entries, category, State.Search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;

        await Show(State with
        {
            Category = category,
            SelectedId = selected,
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"UI Gallery Ready · {State.Entries.Length} controls loaded · Category: {category}",
        });
    }

    [On]
    public Task Filter(GalleryFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawCategory = request.Category?.Trim() ?? string.Empty;
        var viewMode = State.ViewMode;
        if (rawCategory.StartsWith("detail:", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = rawCategory["detail:".Length..];
            viewMode = "detail";
        }
        else if (rawCategory.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = rawCategory["grid:".Length..];
            viewMode = "grid";
        }

        var category = string.IsNullOrWhiteSpace(rawCategory) ? State.Category : GalleryLibrary.NormalizeCategory(rawCategory);
        var search = request.Search is null ? State.Search : request.Search.Trim();
        var matches = GalleryLibrary.FilteredEntries(State.Entries, category, search);
        var selected = matches.Any(e => e.Id == State.SelectedId)
            ? State.SelectedId
            : matches.FirstOrDefault()?.Id ?? State.SelectedId;
        return Show(State with
        {
            Category = category,
            Search = search,
            SelectedId = selected,
            ViewMode = viewMode,
            Status = $"Filtered {matches.Length} control(s) in '{category}'",
        });
    }

    [On]
    public Task Select(GallerySelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ControlId) ? "approval-card" : request.ControlId.Trim();

        if (string.Equals(raw, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "grid", StringComparison.OrdinalIgnoreCase))
        {
            return Show(State with
            {
                ViewMode = "grid",
                Status = $"Catalog Grid · {GalleryLibrary.FilteredEntries(State.Entries, State.Category, State.Search).Length} controls",
            });
        }

        if (string.Equals(raw, "detail", StringComparison.OrdinalIgnoreCase))
        {
            return Show(State with { ViewMode = "detail", Status = $"Inspecting {State.SelectedId}" });
        }

        if (string.Equals(raw, "pulse", StringComparison.OrdinalIgnoreCase))
        {
            var nextPulse = State.SignalPulseCount + 1;
            var currentEntry = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
            return Show(State with
            {
                SignalPulseCount = nextPulse,
                Status = $"Fired [Signal] {currentEntry.Signals.FirstOrDefault() ?? "gallery.select"} (#{nextPulse})",
            });
        }

        if (raw.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
        {
            var tab = raw["tab:".Length..].Trim().ToLowerInvariant();
            return Show(State with { CodeTab = tab is "flutter" or "contract" ? tab : "csharp", Status = $"Code Inspector tab: {tab.ToUpperInvariant()}" });
        }

        if (raw.StartsWith("knob:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var knobKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var knobVal = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            return Show(knobKind switch
            {
                "style" => State with { KnobStyle = knobVal, Status = $"Property Knob · Style = {knobVal}" },
                "tone" => State with { KnobTone = knobVal, Status = $"Property Knob · Tone = {knobVal}" },
                "size" => State with { KnobSmall = knobVal is "small" or "true", Status = $"Property Knob · Size = {(knobVal is "small" or "true" ? "Small (30px)" : "Standard (36px)")}" },
                "state" => State with { KnobState = knobVal, Status = $"Property Knob · State = {knobVal}" },
                "surface" => State with { KnobSurface = knobVal, Status = $"Property Knob · Surface = {knobVal}" },
                "chart" => State with { KnobChartKind = knobVal, Status = $"Property Knob · ChartKind = {knobVal}" },
                _ => State,
            });
        }

        if (raw.StartsWith("toggle:", StringComparison.OrdinalIgnoreCase))
        {
            var target = raw["toggle:".Length..].Trim().ToLowerInvariant();
            return Show(target switch
            {
                "lock" => State with { SwitchLocked = !State.SwitchLocked, Status = State.SwitchLocked ? "Unlocked switch policy" : "Locked switch policy" },
                _ => State with { SwitchValue = !State.SwitchValue, Status = !State.SwitchValue ? "Live [Signal] stream active" : "Stream paused" },
            });
        }

        if (raw.StartsWith("option:", StringComparison.OrdinalIgnoreCase))
        {
            var opt = raw["option:".Length..].Trim();
            return Show(State with { SelectedOption = opt, Status = $"Selected option: {opt}" });
        }

        if (raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var nav = raw["shell:".Length..].Trim().ToLowerInvariant();
            return Show(State with { ShellNav = nav, Status = $"Shell Navigation tab: {nav.ToUpperInvariant()}" });
        }

        if (raw.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var cardKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var cardAct = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            return Show(cardKind switch
            {
                "approval" => State with { ApprovalStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ApprovalCard: {cardAct}" },
                "choice" => State with { ChoicePickedIndex = int.TryParse(cardAct, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : 0, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ChoiceCard picked option #{cardAct}" },
                "draft" => State with { DraftStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"DraftCard: {cardAct}" },
                "secret" => State with { SecretStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"SecretCard: {cardAct}" },
                "job" => State with { JobStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"JobCard: {cardAct}" },
                _ => State,
            });
        }

        if (raw.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            var nodeId = raw["node:".Length..].Trim();
            return Show(State with { SelectedBrainNode = nodeId, Status = $"Inspecting 3D Brain node: {nodeId}" });
        }

        var normalizedId = raw.Equals("brain-3d", StringComparison.OrdinalIgnoreCase)
            ? "brain-scene-3d"
            : raw.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                ? "acrylic-surface"
                : raw;

        var match = State.Entries.FirstOrDefault(e => string.Equals(e.Id, normalizedId, StringComparison.OrdinalIgnoreCase)) ?? State.Entries[0];
        return Show(State with
        {
            SelectedId = match.Id,
            ViewMode = "detail",
            Status = $"Inspecting {match.Title} ({match.NeuronContract})",
        });
    }

    [On]
    public async Task SwitchIdentity(GallerySwitchIdentity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = string.IsNullOrWhiteSpace(request.StylePreset) ? State.StylePreset : request.StylePreset.Trim();
        var barLayout = string.IsNullOrWhiteSpace(request.BarLayout) ? GalleryLibrary.DefaultBarLayoutFor(preset, State.BarLayout) : request.BarLayout.Trim();
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
        {
            var saved = await settings.SetUiIdentity(preset, barLayout);
            preset = saved.StylePreset;
            barLayout = saved.BarLayout;
        }

        await Show(State with
        {
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Active UI Identity: {preset} ({barLayout})",
        });
    }

    public new GalleryAppState State => base.State;
    public View Render() => Render(State);
    protected override View Render(GalleryAppState state) => GalleryRendering.Render(state);

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, $"{typeof(T).Name} is optional when GalleryApp runs in tests.");
            return null;
        }
    }
}
