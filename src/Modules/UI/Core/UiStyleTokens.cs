namespace IntoChat.OS.Modules.UI.Core;

/// <summary>Semantic typography role matching the unified Flutter <c>UiTypography</c> scale.</summary>
public enum UiTypeRole
{
    Body = 0,
    Display = 1,
    Headline = 2,
    Title = 3,
    Label = 4,
    Caption = 5,
    Code = 6,
}

/// <summary>Surface elevation level matching <c>UiElevationScale</c>.</summary>
public enum UiElevation
{
    Flat = 0,
    Subtle = 1,
    Raised = 2,
    Floating = 3,
    Overlay = 4,
}

/// <summary>Motion / animation curve preset used by Flutter <c>UiControlSurface</c>.</summary>
public enum UiMotionPreset
{
    Instant = 0,
    Snappy = 1,
    Standard = 2,
    Expressive = 3,
    Spring = 4,
}

/// <summary>Semantic surface tone matching the unified <c>UiColorScheme</c>.</summary>
public enum UiSurfaceTone
{
    Default = 0,
    Page = 1,
    Canvas = 2,
    Card = 3,
    Subtle = 4,
    Accent = 5,
    Success = 6,
    Warning = 7,
    Danger = 8,
}

/// <summary>
/// Optional state-specific style overrides (hover, press, focus) attached to a <see cref="UiStyleRef"/>.
/// Any <see langword="null"/> property inherits from the base <see cref="UiStyleRef"/>.
/// </summary>
[GenerateSerializer, Alias("ui.core.style-state-override")]
public sealed record UiStyleStateOverride(
    [property: Id(0)] UiSurfaceTone? Tone = null,
    [property: Id(1)] UiTypeRole? Typography = null,
    [property: Id(2)] UiElevation? Elevation = null,
    [property: Id(3)] string? Icon = null,
    [property: Id(4)] UiMotionPreset? Motion = null,
    [property: Id(5)] double? PaddingX = null,
    [property: Id(6)] double? PaddingY = null,
    [property: Id(7)] double? Gap = null,
    [property: Id(8)] double? Radius = null);

/// <summary>
/// Declarative theme, layout, typography, iconography, elevation, and motion attachment
/// carried uniformly by every UI control neuron. When layout values (e.g. <see cref="PaddingX"/>,
/// <see cref="PaddingY"/>, <see cref="Radius"/>) are specified in C#, Flutter's <c>UiStyleMapper</c>
/// renders the exact same pixel metrics via <c>mix</c>.
/// </summary>
[GenerateSerializer, Alias("ui.core.style-ref")]
public sealed record UiStyleRef(
    [property: Id(0)] UiSurfaceTone Tone = UiSurfaceTone.Default,
    [property: Id(1)] UiTypeRole Typography = UiTypeRole.Body,
    [property: Id(2)] UiElevation Elevation = UiElevation.Flat,
    [property: Id(3)] string? Icon = null,
    [property: Id(4)] UiMotionPreset Motion = UiMotionPreset.Standard,
    [property: Id(5)] double? PaddingX = null,
    [property: Id(6)] double? PaddingY = null,
    [property: Id(7)] double? Gap = null,
    [property: Id(8)] double? Radius = null,
    [property: Id(9)] UiStyleStateOverride? Hover = null,
    [property: Id(10)] UiStyleStateOverride? Press = null,
    [property: Id(11)] UiStyleStateOverride? Focus = null);
