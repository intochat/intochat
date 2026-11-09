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
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Gallery;

[DigitalBrain.Signal("gallery.open")]
public sealed record GalleryOpen(string? Category = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.filter")]
public sealed record GalleryFilter(string? Category = null, string? Search = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.select")]
public sealed record GallerySelect(string? ControlId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("gallery.switch-identity")]
public sealed record GallerySwitchIdentity(string? StylePreset = null, string? BarLayout = null) : DigitalBrain.Signal;

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

[DigitalBrain.App("gallery", Accent = "#22C55E", Category = "Studio")]
public sealed class GalleryApp : DigitalBrain.App<GalleryAppState>
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(GalleryApp));

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

    [DigitalBrain.On]
    public async Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = string.IsNullOrWhiteSpace(request.Category) ? "all" : GalleryLibrary.NormalizeCategory(request.Category);
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetSettings() is { } settings)
        {
            var current = await settings.Read();
            preset = current.StylePreset;
            barLayout = current.BarLayout;
        }

        if (TryGetLibrary() is { } library)
        {
            try
            {
                await library.Filter(category, State.Search);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "GalleryLibrary neuron is optional when running GalleryApp standalone.");
            }
        }

        var visibleCount = GalleryLibrary.FilteredEntries(State.Entries, category, State.Search).Length;
        Set(State with
        {
            Category = category,
            StylePreset = preset,
            BarLayout = barLayout,
            Status = $"Showing {visibleCount} UI Kit controls ({preset} · {barLayout})",
        });
    }

    [DigitalBrain.On]
    public void Filter(GalleryFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawCategory = request.Category?.Trim();
        var viewMode = State.ViewMode;
        if (string.Equals(rawCategory, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawCategory, "grid", StringComparison.OrdinalIgnoreCase))
        {
            rawCategory = "all";
            viewMode = "grid";
        }
        else if (rawCategory is not null && rawCategory.StartsWith("grid:", StringComparison.OrdinalIgnoreCase))
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
        Set(State with
        {
            Category = category,
            Search = search,
            SelectedId = selected,
            ViewMode = viewMode,
            Status = $"Filtered {matches.Length} control(s) in '{category}'",
        });
    }

    [DigitalBrain.On]
    public void Select(GallerySelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.IsNullOrWhiteSpace(request.ControlId) ? "approval-card" : request.ControlId.Trim();

        if (string.Equals(raw, "home", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "grid", StringComparison.OrdinalIgnoreCase))
        {
            Set(State with
            {
                ViewMode = "grid",
                Status = $"Catalog Grid · {GalleryLibrary.FilteredEntries(State.Entries, State.Category, State.Search).Length} controls",
            });
            return;
        }

        if (string.Equals(raw, "detail", StringComparison.OrdinalIgnoreCase))
        {
            Set(State with
            {
                ViewMode = "detail",
                Status = $"Inspecting {State.SelectedId}",
            });
            return;
        }

        if (string.Equals(raw, "pulse", StringComparison.OrdinalIgnoreCase))
        {
            var nextPulse = State.SignalPulseCount + 1;
            var currentEntry = State.Entries.FirstOrDefault(e => e.Id == State.SelectedId) ?? State.Entries[0];
            Set(State with
            {
                SignalPulseCount = nextPulse,
                Status = $"Fired [Signal] {currentEntry.Signals.FirstOrDefault() ?? "gallery.select"} (#{nextPulse})",
            });
            return;
        }

        if (raw.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
        {
            var tab = raw["tab:".Length..].Trim().ToLowerInvariant();
            Set(State with
            {
                CodeTab = tab is "flutter" or "contract" ? tab : "csharp",
                Status = $"Code Inspector tab: {tab.ToUpperInvariant()}",
            });
            return;
        }

        if (raw.StartsWith("knob:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var knobKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var knobVal = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            var nextState = knobKind switch
            {
                "style" => State with { KnobStyle = knobVal, Status = $"Property Knob · Style = {knobVal}" },
                "tone" => State with { KnobTone = knobVal, Status = $"Property Knob · Tone = {knobVal}" },
                "size" => State with { KnobSmall = knobVal is "small" or "true", Status = $"Property Knob · Size = {(knobVal is "small" or "true" ? "Small (30px)" : "Standard (36px)")}" },
                "state" => State with { KnobState = knobVal, Status = $"Property Knob · State = {knobVal}" },
                "surface" => State with { KnobSurface = knobVal, Status = $"Property Knob · Surface = {knobVal}" },
                "chart" => State with { KnobChartKind = knobVal, Status = $"Property Knob · ChartKind = {knobVal}" },
                _ => State,
            };
            Set(nextState);
            return;
        }

        if (raw.StartsWith("toggle:", StringComparison.OrdinalIgnoreCase))
        {
            var target = raw["toggle:".Length..].Trim().ToLowerInvariant();
            Set(target switch
            {
                "lock" => State with { SwitchLocked = !State.SwitchLocked, Status = State.SwitchLocked ? "Unlocked switch policy" : "Locked switch policy" },
                _ => State with { SwitchValue = !State.SwitchValue, Status = !State.SwitchValue ? "Live [Signal] stream active" : "Stream paused" },
            });
            return;
        }

        if (raw.StartsWith("option:", StringComparison.OrdinalIgnoreCase))
        {
            var opt = raw["option:".Length..].Trim();
            Set(State with { SelectedOption = opt, Status = $"Selected option: {opt}" });
            return;
        }

        if (raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var nav = raw["shell:".Length..].Trim().ToLowerInvariant();
            Set(State with { ShellNav = nav, Status = $"AppShell pane: {nav}" });
            return;
        }

        if (raw.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            var cardKind = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var cardAct = parts.ElementAtOrDefault(2)?.ToLowerInvariant() ?? "";
            Set(cardKind switch
            {
                "approval" => State with { ApprovalStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ApprovalCard: {cardAct}" },
                "choice" => State with { ChoicePickedIndex = int.TryParse(cardAct, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : 0, SignalPulseCount = State.SignalPulseCount + 1, Status = $"ChoiceCard picked option #{cardAct}" },
                "draft" => State with { DraftStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"DraftCard: {cardAct}" },
                "secret" => State with { SecretStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"SecretCard: {cardAct}" },
                "job" => State with { JobStatus = cardAct, SignalPulseCount = State.SignalPulseCount + 1, Status = $"JobCard: {cardAct}" },
                _ => State,
            });
            return;
        }

        if (raw.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            var nodeId = raw["node:".Length..].Trim();
            Set(State with { SelectedBrainNode = nodeId, Status = $"Inspecting 3D Brain node: {nodeId}" });
            return;
        }

        var normalizedId = raw.Equals("brain-3d", StringComparison.OrdinalIgnoreCase)
            ? "brain-scene-3d"
            : raw.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                ? "acrylic-surface"
                : raw;

        var match = State.Entries.FirstOrDefault(e => string.Equals(e.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? State.Entries[0];
        Set(State with
        {
            SelectedId = match.Id,
            ViewMode = "detail",
            Status = $"Inspecting {match.Title} ({match.NeuronContract})",
        });
    }

    [DigitalBrain.On]
    public async Task SwitchIdentity(GallerySwitchIdentity request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = string.IsNullOrWhiteSpace(request.StylePreset) ? State.StylePreset : request.StylePreset.Trim();
        var barLayout = string.IsNullOrWhiteSpace(request.BarLayout) ? GalleryLibrary.DefaultBarLayoutFor(preset, State.BarLayout) : request.BarLayout.Trim();
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

    public View RenderSide() => GalleryRendering.RenderSide(State);

    public View Render() => GalleryRendering.Render(State);

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

    private IGalleryLibrary? TryGetLibrary()
    {
        try
        {
            return Get<IGalleryLibrary>("library");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "GalleryLibrary neuron is optional when running GalleryApp in isolation.");
            return null;
        }
    }

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
