using Orleans;

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
/// Declarative theme, typography, iconography, elevation, and motion attachment
/// carried uniformly by every UI control neuron.
/// </summary>
[GenerateSerializer, Alias("ui.core.style-ref")]
public sealed record UiStyleRef(
    [property: Id(0)] UiSurfaceTone Tone = UiSurfaceTone.Default,
    [property: Id(1)] UiTypeRole Typography = UiTypeRole.Body,
    [property: Id(2)] UiElevation Elevation = UiElevation.Flat,
    [property: Id(3)] string? Icon = null,
    [property: Id(4)] UiMotionPreset Motion = UiMotionPreset.Standard);
