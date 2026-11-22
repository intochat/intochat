namespace IntoChat.OS.Modules.UI.Core;

/// <summary>
/// Entry point for the fluent C# Mix styling DSL that builds <see cref="UiStyleRef"/> instances.
/// </summary>
public static class Mix
{
    /// <summary>Creates a new fluent <see cref="MixStyleBuilder"/>.</summary>
    public static MixStyleBuilder Style() => new();
}

/// <summary>
/// Fluent builder for <see cref="UiStyleRef"/> and state-specific <see cref="UiStyleStateOverride"/> values.
/// Implicitly converts to <see cref="UiStyleRef"/> so screens can pass <c>Mix.Style()...</c> directly.
/// </summary>
public sealed class MixStyleBuilder
{
    private UiSurfaceTone? _tone;
    private UiTypeRole? _typography;
    private UiElevation? _elevation;
    private string? _icon;
    private UiMotionPreset? _motion;
    private double? _paddingX;
    private double? _paddingY;
    private double? _gap;
    private double? _radius;
    private UiStyleStateOverride? _hover;
    private UiStyleStateOverride? _press;
    private UiStyleStateOverride? _focus;

    /// <summary>Sets the semantic surface tone by name (case-insensitive).</summary>
    public MixStyleBuilder Tone(string tone)
    {
        _tone = ParseEnum<UiSurfaceTone>(tone, nameof(tone));
        return this;
    }

    /// <summary>Sets the semantic surface tone.</summary>
    public MixStyleBuilder Tone(UiSurfaceTone tone)
    {
        _tone = tone;
        return this;
    }

    /// <summary>Sets the semantic typography role by name (case-insensitive).</summary>
    public MixStyleBuilder Typography(string typography)
    {
        _typography = ParseEnum<UiTypeRole>(typography, nameof(typography));
        return this;
    }

    /// <summary>Sets the semantic typography role.</summary>
    public MixStyleBuilder Typography(UiTypeRole typography)
    {
        _typography = typography;
        return this;
    }

    /// <summary>Sets the surface elevation level by name (case-insensitive).</summary>
    public MixStyleBuilder Elevation(string elevation)
    {
        _elevation = ParseEnum<UiElevation>(elevation, nameof(elevation));
        return this;
    }

    /// <summary>Sets the surface elevation level.</summary>
    public MixStyleBuilder Elevation(UiElevation elevation)
    {
        _elevation = elevation;
        return this;
    }

    /// <summary>Sets the optional icon token.</summary>
    public MixStyleBuilder Icon(string? icon)
    {
        _icon = icon;
        return this;
    }

    /// <summary>Sets the motion preset by name (case-insensitive).</summary>
    public MixStyleBuilder Motion(string motion)
    {
        _motion = ParseEnum<UiMotionPreset>(motion, nameof(motion));
        return this;
    }

    /// <summary>Sets the motion preset.</summary>
    public MixStyleBuilder Motion(UiMotionPreset motion)
    {
        _motion = motion;
        return this;
    }

    /// <summary>Sets horizontal and/or vertical padding in logical pixels.</summary>
    public MixStyleBuilder Padding(double? x = null, double? y = null)
    {
        if (x.HasValue)
        {
            _paddingX = x.Value;
        }

        if (y.HasValue)
        {
            _paddingY = y.Value;
        }

        return this;
    }

    /// <summary>Sets child gap spacing in logical pixels.</summary>
    public MixStyleBuilder Gap(double gap)
    {
        _gap = gap;
        return this;
    }

    /// <summary>Sets corner border radius in logical pixels.</summary>
    public MixStyleBuilder Radius(double radius)
    {
        _radius = radius;
        return this;
    }

    /// <summary>Configures style overrides applied while hovered.</summary>
    public MixStyleBuilder OnHover(Func<MixStyleBuilder, MixStyleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _hover = configure(new MixStyleBuilder()).BuildOverride();
        return this;
    }

    /// <summary>Configures style overrides applied while pressed.</summary>
    public MixStyleBuilder OnPress(Func<MixStyleBuilder, MixStyleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _press = configure(new MixStyleBuilder()).BuildOverride();
        return this;
    }

    /// <summary>Configures style overrides applied while focused.</summary>
    public MixStyleBuilder OnFocus(Func<MixStyleBuilder, MixStyleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _focus = configure(new MixStyleBuilder()).BuildOverride();
        return this;
    }

    /// <summary>Builds an immutable <see cref="UiStyleRef"/>.</summary>
    public UiStyleRef Build() => new(
        Tone: _tone ?? UiSurfaceTone.Default,
        Typography: _typography ?? UiTypeRole.Body,
        Elevation: _elevation ?? UiElevation.Flat,
        Icon: _icon,
        Motion: _motion ?? UiMotionPreset.Standard,
        PaddingX: _paddingX,
        PaddingY: _paddingY,
        Gap: _gap,
        Radius: _radius,
        Hover: _hover,
        Press: _press,
        Focus: _focus);

    /// <summary>Builds a <see cref="UiStyleStateOverride"/> containing only explicitly set properties.</summary>
    public UiStyleStateOverride BuildOverride() => new(
        Tone: _tone,
        Typography: _typography,
        Elevation: _elevation,
        Icon: _icon,
        Motion: _motion,
        PaddingX: _paddingX,
        PaddingY: _paddingY,
        Gap: _gap,
        Radius: _radius);

    /// <summary>Implicitly converts a <see cref="MixStyleBuilder"/> to <see cref="UiStyleRef"/>.</summary>
    public static implicit operator UiStyleRef(MixStyleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Build();
    }

    private static TEnum ParseEnum<TEnum>(string value, string paramName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Value for {paramName} cannot be null or whitespace.", paramName);
        }

        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"Unknown {typeof(TEnum).Name} value '{value}'.", paramName);
    }
}
