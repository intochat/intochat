using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>Semantic colour tones mirrored 1:1 by the client's <c>UiStyleMapper</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UiTone>))]
public enum UiTone
{
    /// <summary>Default surface tone.</summary>
    Default,

    /// <summary>Primary brand accent.</summary>
    Accent,

    /// <summary>Emphasised accent tone (for example hover states on accent controls).</summary>
    AccentStrong,

    /// <summary>Soft tinted accent surface.</summary>
    AccentTint,

    /// <summary>Positive / completion tone.</summary>
    Success,

    /// <summary>Warning / in-progress tone.</summary>
    Warning,

    /// <summary>Destructive / error tone.</summary>
    Danger,

    /// <summary>Recessed / subtle surface tone.</summary>
    Subtle,

    /// <summary>Card surface tone.</summary>
    Card,

    /// <summary>Page canvas tone.</summary>
    Canvas,

    /// <summary>Primary action tone.</summary>
    Primary,

    /// <summary>Secondary action tone.</summary>
    Secondary,

    /// <summary>Transparent ghost tone.</summary>
    Ghost,

    /// <summary>Link text tone.</summary>
    Link,

    /// <summary>Elevated surface tone.</summary>
    Elevated,
}

/// <summary>Semantic typography roles mirrored by the client's <c>UiStyleMapper</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UiTypography>))]
public enum UiTypography
{
    /// <summary>Large display headline.</summary>
    Display,

    /// <summary>Section heading.</summary>
    Heading,

    /// <summary>Card or control title.</summary>
    Title,

    /// <summary>Standard body copy.</summary>
    Body,

    /// <summary>Secondary caption text.</summary>
    Caption,

    /// <summary>Control label text.</summary>
    Label,

    /// <summary>Compact uppercase/tabular metadata text.</summary>
    Meta,
}

/// <summary>Semantic elevation tiers mapped to client box shadows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UiElevation>))]
public enum UiElevation
{
    /// <summary>No elevation shadow.</summary>
    Flat,

    /// <summary>Extra-subtle hairline lift.</summary>
    Subtle,

    /// <summary>Raised control or card shadow.</summary>
    Raised,

    /// <summary>Floating popover / toolbar shadow.</summary>
    Floating,

    /// <summary>Modal / sheet overlay shadow.</summary>
    Overlay,
}

/// <summary>Semantic motion curve and duration presets mirrored by <c>UiMotionPreset</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UiMotion>))]
public enum UiMotion
{
    /// <summary>Zero-duration immediate transition.</summary>
    Instant,

    /// <summary>Fast, crisp micro-interaction motion.</summary>
    Snappy,

    /// <summary>Balanced default transition.</summary>
    Standard,

    /// <summary>Longer emphasised transition.</summary>
    Expressive,

    /// <summary>Spring-curved tactile transition.</summary>
    Spring,
}

/// <summary>Entry point for authoring fluent <see cref="UiStyle"/> definitions with hover, press, and focus overrides.</summary>
public static class Mix
{
    /// <summary>Starts a new empty fluent style builder.</summary>
    public static UiStyleBuilder Style() => UiStyleBuilder.Empty;

    /// <summary>Starts a fluent style builder seeded with a legacy preset name (for example <c>"primary"</c> or <c>"secondary"</c>).</summary>
    /// <param name="preset">The preset style name.</param>
    public static UiStyleBuilder Style(string preset) => UiStyle.FromPreset(preset).ToBuilder();

    /// <summary>Starts a fluent style builder seeded from an existing <see cref="UiStyle"/>.</summary>
    /// <param name="baseStyle">The style to refine.</param>
    public static UiStyleBuilder Style(UiStyle baseStyle)
    {
        ArgumentNullException.ThrowIfNull(baseStyle);
        return baseStyle.ToBuilder();
    }
}

/// <summary>Immutable state-specific style override serialized inside <c>hover</c>, <c>press</c>, or <c>focus</c> wire blocks.</summary>
public sealed record UiStyleStateOverride
{
    /// <summary>An empty override with no properties set.</summary>
    public static UiStyleStateOverride Empty { get; } = new();

    /// <summary>Optional semantic tone override.</summary>
    [JsonPropertyName("tone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tone { get; init; }

    /// <summary>Optional typography role override.</summary>
    [JsonPropertyName("typography")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Typography { get; init; }

    /// <summary>Optional elevation tier override.</summary>
    [JsonPropertyName("elevation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Elevation { get; init; }

    /// <summary>Optional icon slug override.</summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    /// <summary>Optional motion preset override.</summary>
    [JsonPropertyName("motion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Motion { get; init; }

    /// <summary>Optional horizontal padding override in logical pixels.</summary>
    [JsonPropertyName("paddingX")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PaddingX { get; init; }

    /// <summary>Optional vertical padding override in logical pixels.</summary>
    [JsonPropertyName("paddingY")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PaddingY { get; init; }

    /// <summary>Optional internal spacing gap override in logical pixels.</summary>
    [JsonPropertyName("gap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Gap { get; init; }

    /// <summary>Optional corner radius override in logical pixels.</summary>
    [JsonPropertyName("radius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Radius { get; init; }

    /// <summary>Optional transform scale override (for example <c>0.97</c> on press).</summary>
    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale { get; init; }

    /// <summary>True when no override fields are set.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Tone is null
        && Typography is null
        && Elevation is null
        && Icon is null
        && Motion is null
        && PaddingX is null
        && PaddingY is null
        && Gap is null
        && Radius is null
        && Scale is null;

    /// <summary>Returns a new <see cref="UiStyleStateOverride"/> with non-null properties from <paramref name="other"/> layered on top.</summary>
    public UiStyleStateOverride Merge(UiStyleStateOverride? other)
    {
        if (other is null || other.IsEmpty)
        {
            return this;
        }

        return this with
        {
            Tone = other.Tone ?? Tone,
            Typography = other.Typography ?? Typography,
            Elevation = other.Elevation ?? Elevation,
            Icon = other.Icon ?? Icon,
            Motion = other.Motion ?? Motion,
            PaddingX = other.PaddingX ?? PaddingX,
            PaddingY = other.PaddingY ?? PaddingY,
            Gap = other.Gap ?? Gap,
            Radius = other.Radius ?? Radius,
            Scale = other.Scale ?? Scale,
        };
    }

    /// <summary>Creates an immutable fluent builder initialised from this override.</summary>
    public UiStyleOverrideBuilder ToBuilder() => new(this);
}

/// <summary>Immutable fluent builder for constructing <see cref="UiStyleStateOverride"/> instances inside <c>OnHover</c>, <c>OnPress</c>, and <c>OnFocus</c>.</summary>
public sealed record UiStyleOverrideBuilder
{
    private readonly UiStyleStateOverride _state;

    /// <summary>An empty override builder.</summary>
    public static UiStyleOverrideBuilder Empty { get; } = new(UiStyleStateOverride.Empty);

    internal UiStyleOverrideBuilder(UiStyleStateOverride state)
    {
        _state = state;
    }

    /// <summary>Sets the semantic tone override.</summary>
    public UiStyleOverrideBuilder Tone(UiTone tone) => new(_state with { Tone = UiStyleWire.FormatEnum(tone) });

    /// <summary>Sets the semantic tone override by wire name.</summary>
    public UiStyleOverrideBuilder Tone(string tone) => new(_state with { Tone = UiStyleWire.RequireToken(tone, nameof(tone)) });

    /// <summary>Sets the typography role override.</summary>
    public UiStyleOverrideBuilder Typography(UiTypography typography) => new(_state with { Typography = UiStyleWire.FormatEnum(typography) });

    /// <summary>Sets the typography role override by wire name.</summary>
    public UiStyleOverrideBuilder Typography(string typography) => new(_state with { Typography = UiStyleWire.RequireToken(typography, nameof(typography)) });

    /// <summary>Sets the elevation tier override.</summary>
    public UiStyleOverrideBuilder Elevation(UiElevation elevation) => new(_state with { Elevation = UiStyleWire.FormatEnum(elevation) });

    /// <summary>Sets the elevation tier override by wire name.</summary>
    public UiStyleOverrideBuilder Elevation(string elevation) => new(_state with { Elevation = UiStyleWire.RequireToken(elevation, nameof(elevation)) });

    /// <summary>Sets the icon slug override.</summary>
    public UiStyleOverrideBuilder Icon(string icon) => new(_state with { Icon = UiStyleWire.RequireToken(icon, nameof(icon)) });

    /// <summary>Sets the motion preset override.</summary>
    public UiStyleOverrideBuilder Motion(UiMotion motion) => new(_state with { Motion = UiStyleWire.FormatEnum(motion) });

    /// <summary>Sets the motion preset override by wire name.</summary>
    public UiStyleOverrideBuilder Motion(string motion) => new(_state with { Motion = UiStyleWire.RequireToken(motion, nameof(motion)) });

    /// <summary>Sets symmetric horizontal and vertical padding overrides.</summary>
    public UiStyleOverrideBuilder Padding(double x, double y) => new(_state with { PaddingX = x, PaddingY = y });

    /// <summary>Sets uniform padding overrides on both axes.</summary>
    public UiStyleOverrideBuilder Padding(double all) => new(_state with { PaddingX = all, PaddingY = all });

    /// <summary>Sets the internal gap override.</summary>
    public UiStyleOverrideBuilder Gap(double gap) => new(_state with { Gap = gap });

    /// <summary>Sets the corner radius override.</summary>
    public UiStyleOverrideBuilder Radius(double radius) => new(_state with { Radius = radius });

    /// <summary>Sets the scale transform override.</summary>
    public UiStyleOverrideBuilder Scale(double scale) => new(_state with { Scale = scale });

    /// <summary>Materializes the immutable <see cref="UiStyleStateOverride"/>.</summary>
    public UiStyleStateOverride Build() => _state;

    /// <summary>Implicitly converts a fluent override builder into its immutable <see cref="UiStyleStateOverride"/>.</summary>
    public static implicit operator UiStyleStateOverride(UiStyleOverrideBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Build();
    }
}

/// <summary>Immutable declarative UI style supporting both legacy string presets (for example <c>"secondary"</c>) and structured Mix tokens with hover, press, and focus overrides.</summary>
[JsonConverter(typeof(UiStyleJsonConverter))]
public sealed record UiStyle
{
    /// <summary>An empty style with no preset or token overrides.</summary>
    public static UiStyle Empty { get; } = new();

    /// <summary>Optional legacy preset style name (for example <c>"primary"</c>, <c>"secondary"</c>, <c>"ghost"</c>, <c>"danger"</c>, <c>"link"</c>).</summary>
    public string? Preset { get; init; }

    /// <summary>Optional semantic tone wire name.</summary>
    public string? Tone { get; init; }

    /// <summary>Optional typography role wire name.</summary>
    public string? Typography { get; init; }

    /// <summary>Optional elevation tier wire name.</summary>
    public string? Elevation { get; init; }

    /// <summary>Optional icon slug.</summary>
    public string? Icon { get; init; }

    /// <summary>Optional motion preset wire name.</summary>
    public string? Motion { get; init; }

    /// <summary>Optional horizontal padding in logical pixels.</summary>
    public double? PaddingX { get; init; }

    /// <summary>Optional vertical padding in logical pixels.</summary>
    public double? PaddingY { get; init; }

    /// <summary>Optional internal spacing gap in logical pixels.</summary>
    public double? Gap { get; init; }

    /// <summary>Optional corner radius in logical pixels.</summary>
    public double? Radius { get; init; }

    /// <summary>Optional transform scale.</summary>
    public double? Scale { get; init; }

    /// <summary>Optional style overrides applied while hovered.</summary>
    public UiStyleStateOverride? Hover { get; init; }

    /// <summary>Optional style overrides applied while pressed.</summary>
    public UiStyleStateOverride? Press { get; init; }

    /// <summary>Optional style overrides applied while focused.</summary>
    public UiStyleStateOverride? Focus { get; init; }

    /// <summary>True when any structured Mix property or state override is set.</summary>
    [JsonIgnore]
    public bool HasStructuredProperties =>
        Tone is not null
        || Typography is not null
        || Elevation is not null
        || Icon is not null
        || Motion is not null
        || PaddingX is not null
        || PaddingY is not null
        || Gap is not null
        || Radius is not null
        || Scale is not null
        || (Hover is not null && !Hover.IsEmpty)
        || (Press is not null && !Press.IsEmpty)
        || (Focus is not null && !Focus.IsEmpty);

    /// <summary>True when this style carries only a plain legacy preset string and no structured Mix properties.</summary>
    [JsonIgnore]
    public bool IsPresetOnly => Preset is not null && !HasStructuredProperties;

    /// <summary>Creates a legacy preset <see cref="UiStyle"/> (for example <c>"secondary"</c>).</summary>
    /// <param name="preset">The preset style name.</param>
    public static UiStyle FromPreset(string preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new UiStyle { Preset = preset };
    }

    /// <summary>Creates an immutable fluent builder initialised from this style.</summary>
    public UiStyleBuilder ToBuilder() => new(this);

    /// <summary>Serializes this style to its compact wire <see cref="JsonElement"/> (either a preset string or a style object).</summary>
    public JsonElement ToWireJsonElement() => JsonSerializer.SerializeToElement(this, UiStyleWire.SerializerOptions);

    /// <summary>Deserializes a wire <see cref="JsonElement"/> (either a preset string or a style object) into a <see cref="UiStyle"/>.</summary>
    public static UiStyle FromWireJsonElement(JsonElement? element, string fallbackPreset = "secondary")
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return FromPreset(fallbackPreset);
        }

        return JsonSerializer.Deserialize<UiStyle>(element.Value.GetRawText(), UiStyleWire.SerializerOptions)
            ?? FromPreset(fallbackPreset);
    }

    /// <summary>Implicitly converts a plain preset string (such as <c>"secondary"</c>) into a <see cref="UiStyle"/>.</summary>
    public static implicit operator UiStyle(string preset) => FromPreset(preset);

    /// <summary>Implicitly converts a fluent <see cref="UiStyleBuilder"/> into an immutable <see cref="UiStyle"/>.</summary>
    public static implicit operator UiStyle(UiStyleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Build();
    }
}

/// <summary>Immutable fluent builder for authoring <see cref="UiStyle"/> instances via <see cref="Mix.Style()"/>.</summary>
[JsonConverter(typeof(UiStyleBuilderJsonConverter))]
public sealed record UiStyleBuilder
{
    private readonly UiStyle _style;

    /// <summary>An empty fluent style builder.</summary>
    public static UiStyleBuilder Empty { get; } = new(UiStyle.Empty);

    internal UiStyleBuilder(UiStyle style)
    {
        _style = style;
    }

    /// <summary>Sets the semantic tone.</summary>
    public UiStyleBuilder Tone(UiTone tone) => new(_style with { Tone = UiStyleWire.FormatEnum(tone) });

    /// <summary>Sets the semantic tone by wire name.</summary>
    public UiStyleBuilder Tone(string tone) => new(_style with { Tone = UiStyleWire.RequireToken(tone, nameof(tone)) });

    /// <summary>Sets the typography role.</summary>
    public UiStyleBuilder Typography(UiTypography typography) => new(_style with { Typography = UiStyleWire.FormatEnum(typography) });

    /// <summary>Sets the typography role by wire name.</summary>
    public UiStyleBuilder Typography(string typography) => new(_style with { Typography = UiStyleWire.RequireToken(typography, nameof(typography)) });

    /// <summary>Sets the elevation tier.</summary>
    public UiStyleBuilder Elevation(UiElevation elevation) => new(_style with { Elevation = UiStyleWire.FormatEnum(elevation) });

    /// <summary>Sets the elevation tier by wire name.</summary>
    public UiStyleBuilder Elevation(string elevation) => new(_style with { Elevation = UiStyleWire.RequireToken(elevation, nameof(elevation)) });

    /// <summary>Sets the icon slug.</summary>
    public UiStyleBuilder Icon(string icon) => new(_style with { Icon = UiStyleWire.RequireToken(icon, nameof(icon)) });

    /// <summary>Sets the motion preset.</summary>
    public UiStyleBuilder Motion(UiMotion motion) => new(_style with { Motion = UiStyleWire.FormatEnum(motion) });

    /// <summary>Sets the motion preset by wire name.</summary>
    public UiStyleBuilder Motion(string motion) => new(_style with { Motion = UiStyleWire.RequireToken(motion, nameof(motion)) });

    /// <summary>Sets symmetric horizontal (<paramref name="x"/>) and vertical (<paramref name="y"/>) padding in logical pixels.</summary>
    public UiStyleBuilder Padding(double x, double y) => new(_style with { PaddingX = x, PaddingY = y });

    /// <summary>Sets uniform padding on both axes in logical pixels.</summary>
    public UiStyleBuilder Padding(double all) => new(_style with { PaddingX = all, PaddingY = all });

    /// <summary>Sets the internal spacing gap in logical pixels.</summary>
    public UiStyleBuilder Gap(double gap) => new(_style with { Gap = gap });

    /// <summary>Sets the corner radius in logical pixels.</summary>
    public UiStyleBuilder Radius(double radius) => new(_style with { Radius = radius });

    /// <summary>Sets the transform scale.</summary>
    public UiStyleBuilder Scale(double scale) => new(_style with { Scale = scale });

    /// <summary>Configures style overrides applied while the pointer hovers the control.</summary>
    public UiStyleBuilder OnHover(Func<UiStyleOverrideBuilder, UiStyleOverrideBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var next = configure((_style.Hover ?? UiStyleStateOverride.Empty).ToBuilder()).Build();
        return new(_style with { Hover = next.IsEmpty ? null : next });
    }

    /// <summary>Merges an explicit hover state override onto this style.</summary>
    public UiStyleBuilder OnHover(UiStyleStateOverride stateOverride)
    {
        ArgumentNullException.ThrowIfNull(stateOverride);
        var next = (_style.Hover ?? UiStyleStateOverride.Empty).Merge(stateOverride);
        return new(_style with { Hover = next.IsEmpty ? null : next });
    }

    /// <summary>Configures style overrides applied while the control is pressed.</summary>
    public UiStyleBuilder OnPress(Func<UiStyleOverrideBuilder, UiStyleOverrideBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var next = configure((_style.Press ?? UiStyleStateOverride.Empty).ToBuilder()).Build();
        return new(_style with { Press = next.IsEmpty ? null : next });
    }

    /// <summary>Merges an explicit press state override onto this style.</summary>
    public UiStyleBuilder OnPress(UiStyleStateOverride stateOverride)
    {
        ArgumentNullException.ThrowIfNull(stateOverride);
        var next = (_style.Press ?? UiStyleStateOverride.Empty).Merge(stateOverride);
        return new(_style with { Press = next.IsEmpty ? null : next });
    }

    /// <summary>Configures style overrides applied while the control has focus.</summary>
    public UiStyleBuilder OnFocus(Func<UiStyleOverrideBuilder, UiStyleOverrideBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var next = configure((_style.Focus ?? UiStyleStateOverride.Empty).ToBuilder()).Build();
        return new(_style with { Focus = next.IsEmpty ? null : next });
    }

    /// <summary>Merges an explicit focus state override onto this style.</summary>
    public UiStyleBuilder OnFocus(UiStyleStateOverride stateOverride)
    {
        ArgumentNullException.ThrowIfNull(stateOverride);
        var next = (_style.Focus ?? UiStyleStateOverride.Empty).Merge(stateOverride);
        return new(_style with { Focus = next.IsEmpty ? null : next });
    }

    /// <summary>Materializes the immutable <see cref="UiStyle"/>.</summary>
    public UiStyle Build() => _style;
}

internal static class UiStyleWire
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string FormatEnum<TEnum>(TEnum value) where TEnum : struct, Enum
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    internal static string RequireToken(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Style token cannot be null or whitespace.", paramName);
        }

        return value.Trim();
    }

    internal static void WriteStyle(Utf8JsonWriter writer, UiStyle value)
    {
        if (value.IsPresetOnly)
        {
            writer.WriteStringValue(value.Preset);
            return;
        }

        writer.WriteStartObject();
        if (value.Preset is not null)
        {
            writer.WriteString("preset", value.Preset);
        }

        WriteCommonProperties(
            writer,
            value.Tone,
            value.Typography,
            value.Elevation,
            value.Icon,
            value.Motion,
            value.PaddingX,
            value.PaddingY,
            value.Gap,
            value.Radius,
            value.Scale);

        WriteOverride(writer, "hover", value.Hover);
        WriteOverride(writer, "press", value.Press);
        WriteOverride(writer, "focus", value.Focus);
        writer.WriteEndObject();
    }

    internal static UiStyle? ReadStyle(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return UiStyle.FromPreset(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected string or object for UiStyle, got {reader.TokenType}.");
        }

        string? preset = null;
        string? tone = null;
        string? typography = null;
        string? elevation = null;
        string? icon = null;
        string? motion = null;
        double? paddingX = null;
        double? paddingY = null;
        double? gap = null;
        double? radius = null;
        double? scale = null;
        UiStyleStateOverride? hover = null;
        UiStyleStateOverride? press = null;
        UiStyleStateOverride? focus = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new UiStyle
                {
                    Preset = preset,
                    Tone = tone,
                    Typography = typography,
                    Elevation = elevation,
                    Icon = icon,
                    Motion = motion,
                    PaddingX = paddingX,
                    PaddingY = paddingY,
                    Gap = gap,
                    Radius = radius,
                    Scale = scale,
                    Hover = hover is { IsEmpty: false } ? hover : null,
                    Press = press is { IsEmpty: false } ? press : null,
                    Focus = focus is { IsEmpty: false } ? focus : null,
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected property name in UiStyle, got {reader.TokenType}.");
            }

            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "preset":
                    preset = ReadNullableString(ref reader);
                    break;
                case "tone":
                    tone = ReadNullableString(ref reader);
                    break;
                case "typography":
                    typography = ReadNullableString(ref reader);
                    break;
                case "elevation":
                    elevation = ReadNullableString(ref reader);
                    break;
                case "icon":
                    icon = ReadNullableString(ref reader);
                    break;
                case "motion":
                    motion = ReadNullableString(ref reader);
                    break;
                case "paddingX":
                case "px":
                    paddingX = ReadNullableDouble(ref reader);
                    break;
                case "paddingY":
                case "py":
                    paddingY = ReadNullableDouble(ref reader);
                    break;
                case "gap":
                    gap = ReadNullableDouble(ref reader);
                    break;
                case "radius":
                    radius = ReadNullableDouble(ref reader);
                    break;
                case "scale":
                    scale = ReadNullableDouble(ref reader);
                    break;
                case "hover":
                    hover = ReadOverride(ref reader);
                    break;
                case "press":
                    press = ReadOverride(ref reader);
                    break;
                case "focus":
                    focus = ReadOverride(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Unexpected end of JSON while reading UiStyle.");
    }

    private static void WriteOverride(Utf8JsonWriter writer, string propertyName, UiStyleStateOverride? stateOverride)
    {
        if (stateOverride is null || stateOverride.IsEmpty)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        WriteCommonProperties(
            writer,
            stateOverride.Tone,
            stateOverride.Typography,
            stateOverride.Elevation,
            stateOverride.Icon,
            stateOverride.Motion,
            stateOverride.PaddingX,
            stateOverride.PaddingY,
            stateOverride.Gap,
            stateOverride.Radius,
            stateOverride.Scale);
        writer.WriteEndObject();
    }

    private static void WriteCommonProperties(
        Utf8JsonWriter writer,
        string? tone,
        string? typography,
        string? elevation,
        string? icon,
        string? motion,
        double? paddingX,
        double? paddingY,
        double? gap,
        double? radius,
        double? scale)
    {
        if (tone is not null) writer.WriteString("tone", tone);
        if (typography is not null) writer.WriteString("typography", typography);
        if (elevation is not null) writer.WriteString("elevation", elevation);
        if (icon is not null) writer.WriteString("icon", icon);
        if (motion is not null) writer.WriteString("motion", motion);
        if (paddingX is not null) writer.WriteNumber("paddingX", paddingX.Value);
        if (paddingY is not null) writer.WriteNumber("paddingY", paddingY.Value);
        if (gap is not null) writer.WriteNumber("gap", gap.Value);
        if (radius is not null) writer.WriteNumber("radius", radius.Value);
        if (scale is not null) writer.WriteNumber("scale", scale.Value);
    }

    private static UiStyleStateOverride? ReadOverride(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected object for UiStyleStateOverride, got {reader.TokenType}.");
        }

        string? tone = null;
        string? typography = null;
        string? elevation = null;
        string? icon = null;
        string? motion = null;
        double? paddingX = null;
        double? paddingY = null;
        double? gap = null;
        double? radius = null;
        double? scale = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new UiStyleStateOverride
                {
                    Tone = tone,
                    Typography = typography,
                    Elevation = elevation,
                    Icon = icon,
                    Motion = motion,
                    PaddingX = paddingX,
                    PaddingY = paddingY,
                    Gap = gap,
                    Radius = radius,
                    Scale = scale,
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected property name in UiStyleStateOverride, got {reader.TokenType}.");
            }

            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "tone":
                    tone = ReadNullableString(ref reader);
                    break;
                case "typography":
                    typography = ReadNullableString(ref reader);
                    break;
                case "elevation":
                    elevation = ReadNullableString(ref reader);
                    break;
                case "icon":
                    icon = ReadNullableString(ref reader);
                    break;
                case "motion":
                    motion = ReadNullableString(ref reader);
                    break;
                case "paddingX":
                case "px":
                    paddingX = ReadNullableDouble(ref reader);
                    break;
                case "paddingY":
                case "py":
                    paddingY = ReadNullableDouble(ref reader);
                    break;
                case "gap":
                    gap = ReadNullableDouble(ref reader);
                    break;
                case "radius":
                    radius = ReadNullableDouble(ref reader);
                    break;
                case "scale":
                    scale = ReadNullableDouble(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Unexpected end of JSON while reading UiStyleStateOverride.");
    }

    private static string? ReadNullableString(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

    private static double? ReadNullableDouble(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();
}

/// <summary>Serializes <see cref="UiStyle"/> as either a plain preset string or a compact JSON style block.</summary>
public sealed class UiStyleJsonConverter : JsonConverter<UiStyle>
{
    /// <inheritdoc />
    public override UiStyle? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => UiStyleWire.ReadStyle(ref reader);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UiStyle value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        UiStyleWire.WriteStyle(writer, value);
    }
}

/// <summary>Serializes <see cref="UiStyleBuilder"/> identically to its materialized <see cref="UiStyle"/>.</summary>
public sealed class UiStyleBuilderJsonConverter : JsonConverter<UiStyleBuilder>
{
    /// <inheritdoc />
    public override UiStyleBuilder? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => UiStyleWire.ReadStyle(ref reader)?.ToBuilder();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UiStyleBuilder value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        UiStyleWire.WriteStyle(writer, value.Build());
    }
}
