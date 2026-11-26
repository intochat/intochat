using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.UI;

namespace DigitalBrain.Tests.Unit.UI;

public sealed class MixStyleFacts
{
    [Fact]
    public void FluentBuilderPopulatesAllPropertiesAndStateOverrides()
    {
        UiStyle style = Mix.Style()
            .Tone(UiTone.Accent)
            .Typography(UiTypography.Title)
            .Elevation(UiElevation.Raised)
            .Icon("sparkles")
            .Motion(UiMotion.Snappy)
            .Padding(x: 16, y: 10)
            .Gap(8)
            .Radius(12)
            .Scale(1.0)
            .OnHover(s => s.Tone(UiTone.AccentStrong).Elevation(UiElevation.Floating).Scale(1.02))
            .OnPress(s => s.Tone(UiTone.Danger).Padding(14, 8).Scale(0.97))
            .OnFocus(s => s.Elevation(UiElevation.Raised).Radius(14).Gap(10));

        Assert.Equal("accent", style.Tone);
        Assert.Equal("title", style.Typography);
        Assert.Equal("raised", style.Elevation);
        Assert.Equal("sparkles", style.Icon);
        Assert.Equal("snappy", style.Motion);
        Assert.Equal(16, style.PaddingX);
        Assert.Equal(10, style.PaddingY);
        Assert.Equal(8, style.Gap);
        Assert.Equal(12, style.Radius);
        Assert.Equal(1.0, style.Scale);
        Assert.False(style.IsPresetOnly);
        Assert.True(style.HasStructuredProperties);

        Assert.NotNull(style.Hover);
        Assert.Equal("accentStrong", style.Hover.Tone);
        Assert.Equal("floating", style.Hover.Elevation);
        Assert.Equal(1.02, style.Hover.Scale);

        Assert.NotNull(style.Press);
        Assert.Equal("danger", style.Press.Tone);
        Assert.Equal(14, style.Press.PaddingX);
        Assert.Equal(8, style.Press.PaddingY);
        Assert.Equal(0.97, style.Press.Scale);

        Assert.NotNull(style.Focus);
        Assert.Equal("raised", style.Focus.Elevation);
        Assert.Equal(14, style.Focus.Radius);
        Assert.Equal(10, style.Focus.Gap);
    }

    [Fact]
    public void UniformPaddingSetsBothPaddingXAndPaddingY()
    {
        UiStyle style = Mix.Style().Padding(14).OnHover(s => s.Padding(18));

        Assert.Equal(14, style.PaddingX);
        Assert.Equal(14, style.PaddingY);
        Assert.Equal(18, style.Hover!.PaddingX);
        Assert.Equal(18, style.Hover.PaddingY);
    }

    [Fact]
    public void BuilderIsImmutableAcrossBranches()
    {
        var baseBuilder = Mix.Style().Tone(UiTone.Accent).Padding(12, 8);
        var rounded = baseBuilder.Radius(10).OnHover(s => s.Scale(1.03));
        var sharp = baseBuilder.Radius(2).OnPress(s => s.Scale(0.96));

        var baseStyle = baseBuilder.Build();
        var roundedStyle = rounded.Build();
        var sharpStyle = sharp.Build();

        Assert.Null(baseStyle.Radius);
        Assert.Null(baseStyle.Hover);
        Assert.Null(baseStyle.Press);

        Assert.Equal(10, roundedStyle.Radius);
        Assert.Equal(1.03, roundedStyle.Hover!.Scale);
        Assert.Null(roundedStyle.Press);

        Assert.Equal(2, sharpStyle.Radius);
        Assert.Null(sharpStyle.Hover);
        Assert.Equal(0.96, sharpStyle.Press!.Scale);
    }

    [Fact]
    public void RepeatedStateOverridesMergeInsteadOfOverwriting()
    {
        UiStyle style = Mix.Style()
            .OnHover(s => s.Tone(UiTone.AccentStrong))
            .OnHover(s => s.Elevation(UiElevation.Raised).Scale(1.02))
            .OnPress(new UiStyleStateOverride { Scale = 0.97 })
            .OnPress(s => s.Motion(UiMotion.Snappy))
            .OnFocus(s => s.Radius(10))
            .OnFocus(new UiStyleStateOverride { Elevation = "subtle" });

        Assert.Equal("accentStrong", style.Hover!.Tone);
        Assert.Equal("raised", style.Hover.Elevation);
        Assert.Equal(1.02, style.Hover.Scale);

        Assert.Equal(0.97, style.Press!.Scale);
        Assert.Equal("snappy", style.Press.Motion);

        Assert.Equal(10, style.Focus!.Radius);
        Assert.Equal("subtle", style.Focus.Elevation);
    }

    [Fact]
    public void SerializesCompactJsonWireBlockMatchingFlutterUiStyleData()
    {
        UiStyle style = Mix.Style()
            .Tone(UiTone.Accent)
            .Padding(x: 16, y: 10)
            .Radius(8)
            .Elevation(UiElevation.Raised)
            .Motion(UiMotion.Snappy)
            .OnHover(s => s.Tone(UiTone.AccentStrong))
            .OnPress(s => s.Scale(0.97))
            .OnFocus(s => s.Elevation(UiElevation.Floating));

        var element = JsonSerializer.SerializeToElement(style);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("accent", element.GetProperty("tone").GetString());
        Assert.Equal(16, element.GetProperty("paddingX").GetDouble());
        Assert.Equal(10, element.GetProperty("paddingY").GetDouble());
        Assert.Equal(8, element.GetProperty("radius").GetDouble());
        Assert.Equal("raised", element.GetProperty("elevation").GetString());
        Assert.Equal("snappy", element.GetProperty("motion").GetString());
        Assert.False(element.TryGetProperty("typography", out _));
        Assert.False(element.TryGetProperty("icon", out _));
        Assert.False(element.TryGetProperty("gap", out _));
        Assert.False(element.TryGetProperty("scale", out _));

        var hover = element.GetProperty("hover");
        Assert.Equal("accentStrong", hover.GetProperty("tone").GetString());
        Assert.False(hover.TryGetProperty("scale", out _));

        var press = element.GetProperty("press");
        Assert.Equal(0.97, press.GetProperty("scale").GetDouble());

        var focus = element.GetProperty("focus");
        Assert.Equal("floating", focus.GetProperty("elevation").GetString());

        var roundTripped = JsonSerializer.Deserialize<UiStyle>(element.GetRawText());
        Assert.Equal(style, roundTripped);
    }

    [Fact]
    public void LegacyStringPresetSerializesAsPlainStringAndRoundTrips()
    {
        UiStyle preset = "secondary";

        Assert.True(preset.IsPresetOnly);
        Assert.Equal("secondary", preset.Preset);

        var json = JsonSerializer.Serialize(preset);
        Assert.Equal("\"secondary\"", json);

        var roundTripped = JsonSerializer.Deserialize<UiStyle>(json);
        Assert.Equal(preset, roundTripped);
    }

    [Fact]
    public void ButtonRetainsPlainStringStyleBackwardCompatibility()
    {
        var options = RuntimeProtocol.OptionsFor(typeof(View));
        var defaultBtn = new Button("ok", "OK", new Command(".", "ok"));
        var ghostBtn = new Button("cancel", "Cancel", new Command(".", "cancel")) { Style = "ghost" };

        var defaultWire = JsonSerializer.SerializeToElement<Node>(defaultBtn, options);
        Assert.Equal(JsonValueKind.String, defaultWire.GetProperty("style").ValueKind);
        Assert.Equal("secondary", defaultWire.GetProperty("style").GetString());

        var ghostWire = JsonSerializer.SerializeToElement<Node>(ghostBtn, options);
        Assert.Equal(JsonValueKind.String, ghostWire.GetProperty("style").ValueKind);
        Assert.Equal("ghost", ghostWire.GetProperty("style").GetString());

        Assert.Equal(defaultBtn, JsonSerializer.Deserialize<Node>(defaultWire.GetRawText(), options));
        Assert.Equal(ghostBtn, JsonSerializer.Deserialize<Node>(ghostWire.GetRawText(), options));
    }

    [Fact]
    public void ButtonSerializesAndRoundTripsStructuredMixStyle()
    {
        var options = RuntimeProtocol.OptionsFor(typeof(View));
        var button = new Button("save", "Save Changes", new Command(".", "settings.save"))
        {
            Style = Mix.Style()
                .Tone(UiTone.Accent)
                .Padding(x: 16, y: 10)
                .Radius(8)
                .Elevation(UiElevation.Raised)
                .Motion(UiMotion.Snappy)
                .OnHover(s => s.Tone(UiTone.AccentStrong))
                .OnPress(s => s.Scale(0.97))
                .OnFocus(s => s.Elevation(UiElevation.Floating)),
        };

        var wire = JsonSerializer.SerializeToElement<Node>(button, options);
        var styleWire = wire.GetProperty("style");

        Assert.Equal(JsonValueKind.Object, styleWire.ValueKind);
        Assert.Equal("accent", styleWire.GetProperty("tone").GetString());
        Assert.Equal(16, styleWire.GetProperty("paddingX").GetDouble());
        Assert.Equal(10, styleWire.GetProperty("paddingY").GetDouble());
        Assert.Equal(8, styleWire.GetProperty("radius").GetDouble());
        Assert.Equal("raised", styleWire.GetProperty("elevation").GetString());
        Assert.Equal("snappy", styleWire.GetProperty("motion").GetString());
        Assert.Equal("accentStrong", styleWire.GetProperty("hover").GetProperty("tone").GetString());
        Assert.Equal(0.97, styleWire.GetProperty("press").GetProperty("scale").GetDouble());
        Assert.Equal("floating", styleWire.GetProperty("focus").GetProperty("elevation").GetString());

        var restored = Assert.IsType<Button>(JsonSerializer.Deserialize<Node>(wire.GetRawText(), options));
        Assert.Equal(button, restored);
    }

    [Fact]
    public void DeserializerAcceptsCompactPxPyAliases()
    {
        const string json = """{"tone":"accent","px":16,"py":10,"radius":8,"hover":{"px":20,"py":12}}""";

        var style = JsonSerializer.Deserialize<UiStyle>(json);

        Assert.NotNull(style);
        Assert.Equal("accent", style.Tone);
        Assert.Equal(16, style.PaddingX);
        Assert.Equal(10, style.PaddingY);
        Assert.Equal(8, style.Radius);
        Assert.Equal(20, style.Hover!.PaddingX);
        Assert.Equal(12, style.Hover.PaddingY);
    }

    [Fact]
    public void BuilderDirectSerializationMatchesBuiltStyle()
    {
        var builder = Mix.Style()
            .Tone("accent")
            .Typography("label")
            .Elevation("subtle")
            .Icon("check")
            .Motion("spring")
            .Padding(12, 6)
            .Gap(6)
            .Radius(999)
            .Scale(1.0);

        var fromBuilder = JsonSerializer.Serialize(builder);
        var fromStyle = JsonSerializer.Serialize(builder.Build());

        Assert.Equal(fromStyle, fromBuilder);
    }

    [Fact]
    public void BlankStringTokensThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Mix.Style().Tone(" "));
        Assert.Throws<ArgumentException>(() => Mix.Style().Typography(""));
        Assert.Throws<ArgumentException>(() => Mix.Style().Elevation("\t"));
        Assert.Throws<ArgumentException>(() => Mix.Style().Icon(" "));
        Assert.Throws<ArgumentException>(() => Mix.Style().Motion(""));
        Assert.Throws<ArgumentException>(() => Mix.Style().OnHover(s => s.Tone(" ")));
    }
}
