using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>Semantic button style presets emitted as typed <see cref="Mix.Style()"/> blocks.</summary>
public enum ButtonMixPreset
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}

/// <summary>
/// Shared fluent <see cref="Mix.Style()"/> tokens for IntoChat OS surfaces and controls,
/// aligned with <c>ui/lib/src/theme/ui_tokens.dart</c> and <c>ui/lib/src/primitives/style.dart</c>.
/// </summary>
public static class OsMixStyles
{
    /// <summary>Single primary CTA style of a surface.</summary>
    public static UiStyleBuilder PrimaryButton(bool small = false)
        => Mix.Style()
            .Tone(UiTone.Accent)
            .Typography(UiTypography.Label)
            .Padding(x: small ? 10 : 14, y: small ? 5 : 8)
            .Radius(small ? 8 : 10)
            .Elevation(UiElevation.Subtle)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Tone(UiTone.AccentStrong).Elevation(UiElevation.Raised))
            .OnPress(s => s.Scale(0.97))
            .OnFocus(s => s.Elevation(UiElevation.Raised));

    /// <summary>Standard secondary desktop action button.</summary>
    public static UiStyleBuilder SecondaryButton(bool small = false)
        => Mix.Style()
            .Tone(UiTone.Secondary)
            .Typography(UiTypography.Label)
            .Padding(x: small ? 10 : 14, y: small ? 5 : 8)
            .Radius(small ? 8 : 10)
            .Elevation(UiElevation.Flat)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Tone(UiTone.Subtle))
            .OnPress(s => s.Scale(0.97))
            .OnFocus(s => s.Elevation(UiElevation.Subtle));

    /// <summary>Quiet borderless toolbar/inline action button.</summary>
    public static UiStyleBuilder GhostButton(bool small = false)
        => Mix.Style()
            .Tone(UiTone.Ghost)
            .Typography(UiTypography.Label)
            .Padding(x: small ? 9 : 12, y: small ? 5 : 7)
            .Radius(small ? 8 : 10)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Tone(UiTone.Subtle))
            .OnPress(s => s.Scale(0.97));

    /// <summary>Destructive action button with danger tone and elevated hover feedback.</summary>
    public static UiStyleBuilder DangerButton(bool small = false)
        => Mix.Style()
            .Tone(UiTone.Danger)
            .Typography(UiTypography.Label)
            .Padding(x: small ? 10 : 14, y: small ? 5 : 8)
            .Radius(small ? 8 : 10)
            .Elevation(UiElevation.Subtle)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Elevation(UiElevation.Raised))
            .OnPress(s => s.Scale(0.97))
            .OnFocus(s => s.Elevation(UiElevation.Raised));

    /// <summary>Returns the canonical <see cref="UiStyleBuilder"/> for a <see cref="ButtonMixPreset"/>.</summary>
    public static UiStyleBuilder ForButtonPreset(ButtonMixPreset preset, bool small = false) => preset switch
    {
        ButtonMixPreset.Primary => PrimaryButton(small),
        ButtonMixPreset.Ghost => GhostButton(small),
        ButtonMixPreset.Danger => DangerButton(small),
        _ => SecondaryButton(small),
    };

    /// <summary>High-emphasis hero call-to-action with spring motion and floating hover lift.</summary>
    public static UiStyleBuilder HeroCta()
        => Mix.Style()
            .Tone(UiTone.Accent)
            .Typography(UiTypography.Label)
            .Padding(x: 18, y: 10)
            .Radius(10)
            .Elevation(UiElevation.Raised)
            .Motion(UiMotion.Spring)
            .OnHover(s => s.Tone(UiTone.AccentStrong).Elevation(UiElevation.Floating))
            .OnPress(s => s.Scale(0.97))
            .OnFocus(s => s.Elevation(UiElevation.Floating));

    /// <summary>Selectable pill button for segmented filters, view switches, and tab strips.</summary>
    public static UiStyleBuilder FilterPill(bool selected)
        => selected
            ? Mix.Style()
                .Tone(UiTone.AccentTint)
                .Typography(UiTypography.Label)
                .Padding(x: 12, y: 5)
                .Radius(999)
                .Elevation(UiElevation.Flat)
                .Motion(UiMotion.Snappy)
                .OnHover(s => s.Tone(UiTone.Accent))
                .OnPress(s => s.Scale(0.97))
                .OnFocus(s => s.Elevation(UiElevation.Subtle))
            : Mix.Style()
                .Tone(UiTone.Ghost)
                .Typography(UiTypography.Label)
                .Padding(x: 12, y: 5)
                .Radius(999)
                .Motion(UiMotion.Snappy)
                .OnHover(s => s.Tone(UiTone.Subtle))
                .OnPress(s => s.Scale(0.97));

    /// <summary>Compact inline navigation or cross-app link button.</summary>
    public static UiStyleBuilder LinkAction(bool small = true)
        => Mix.Style()
            .Tone(UiTone.Link)
            .Typography(UiTypography.Label)
            .Padding(x: small ? 8 : 10, y: small ? 4 : 6)
            .Radius(8)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Tone(UiTone.AccentTint))
            .OnPress(s => s.Scale(0.97));

    /// <summary>Icon-only toolbar / chrome button with snappy micro-scale press feedback.</summary>
    public static UiStyleBuilder IconButton()
        => Mix.Style()
            .Tone(UiTone.Ghost)
            .Padding(6)
            .Radius(8)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Tone(UiTone.Subtle))
            .OnPress(s => s.Scale(0.94))
            .OnFocus(s => s.Elevation(UiElevation.Subtle));

    /// <summary>Structured card container style with warm surface tokens and 14px desktop radius.</summary>
    public static UiStyleBuilder Card(bool accent = false, bool elevated = false)
        => Mix.Style()
            .Tone(accent ? UiTone.AccentTint : UiTone.Card)
            .Elevation(elevated ? UiElevation.Raised : UiElevation.Subtle)
            .Padding(16)
            .Radius(14);

    /// <summary>Structured surface container style aligned with <see cref="SurfaceKind"/>.</summary>
    public static UiStyleBuilder Surface(string kind = SurfaceKind.Elevated, double padding = 18) => kind switch
    {
        SurfaceKind.Recessed => Mix.Style()
            .Tone(UiTone.Subtle)
            .Elevation(UiElevation.Flat)
            .Padding(padding)
            .Radius(14),
        SurfaceKind.Subtle => Mix.Style()
            .Tone(UiTone.Subtle)
            .Elevation(UiElevation.Flat)
            .Padding(padding)
            .Radius(14),
        SurfaceKind.Acrylic => Mix.Style()
            .Tone(UiTone.Elevated)
            .Elevation(UiElevation.Subtle)
            .Padding(padding)
            .Radius(18),
        _ => Mix.Style()
            .Tone(UiTone.Elevated)
            .Elevation(UiElevation.Raised)
            .Padding(padding)
            .Radius(18),
    };
}
