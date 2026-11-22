using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.UI.Core;

namespace IntoChat.OS.Tests.Unit.Modules.UI;

public sealed class MixStyleBuilderFacts
{
    [Fact]
    public void FluentChainingBuildsExpectedUiStyleRefWithStateOverrides()
    {
        var style = Mix.Style()
            .Tone("accent")
            .Typography("label")
            .Elevation("raised")
            .Icon("sparkles")
            .Motion("spring")
            .Padding(x: 16, y: 10)
            .Gap(8)
            .Radius(12)
            .OnHover(s => s.Tone("subtle").Elevation("floating"))
            .OnPress(s => s.Elevation("subtle").Radius(10))
            .OnFocus(s => s.Tone("warning").Padding(x: 18))
            .Build();

        Assert.Equal(UiSurfaceTone.Accent, style.Tone);
        Assert.Equal(UiTypeRole.Label, style.Typography);
        Assert.Equal(UiElevation.Raised, style.Elevation);
        Assert.Equal("sparkles", style.Icon);
        Assert.Equal(UiMotionPreset.Spring, style.Motion);
        Assert.Equal(16, style.PaddingX);
        Assert.Equal(10, style.PaddingY);
        Assert.Equal(8, style.Gap);
        Assert.Equal(12, style.Radius);

        Assert.NotNull(style.Hover);
        Assert.Equal(UiSurfaceTone.Subtle, style.Hover.Tone);
        Assert.Equal(UiElevation.Floating, style.Hover.Elevation);
        Assert.Null(style.Hover.Typography);
        Assert.Null(style.Hover.Radius);

        Assert.NotNull(style.Press);
        Assert.Equal(UiElevation.Subtle, style.Press.Elevation);
        Assert.Equal(10, style.Press.Radius);
        Assert.Null(style.Press.Tone);

        Assert.NotNull(style.Focus);
        Assert.Equal(UiSurfaceTone.Warning, style.Focus.Tone);
        Assert.Equal(18, style.Focus.PaddingX);
        Assert.Null(style.Focus.PaddingY);
    }

    [Fact]
    public void ImplicitConversionProducesUiStyleRefDirectly()
    {
        UiStyleRef style = Mix.Style().Tone("accent").Radius(8);

        Assert.Equal(UiSurfaceTone.Accent, style.Tone);
        Assert.Equal(UiTypeRole.Body, style.Typography);
        Assert.Equal(UiElevation.Flat, style.Elevation);
        Assert.Equal(UiMotionPreset.Standard, style.Motion);
        Assert.Equal(8, style.Radius);
        Assert.Null(style.Hover);
        Assert.Null(style.Press);
        Assert.Null(style.Focus);
    }

    [Fact]
    public void JsonRoundTripPreservesBaseAndStateOverrides()
    {
        UiStyleRef original = Mix.Style()
            .Tone("card")
            .Typography("title")
            .Elevation("subtle")
            .Motion("snappy")
            .Padding(x: 12, y: 8)
            .Gap(6)
            .Radius(14)
            .OnHover(s => s.Tone("accent").Elevation("raised"))
            .OnPress(s => s.Elevation("flat").Motion("instant"))
            .OnFocus(s => s.Tone("success").Radius(16));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<UiStyleRef>(json, options);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void InvalidTokenNameThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Mix.Style().Tone("nonexistent"));
        Assert.Throws<ArgumentException>(() => Mix.Style().Typography("unknown"));
        Assert.Throws<ArgumentException>(() => Mix.Style().Elevation("skyhigh"));
        Assert.Throws<ArgumentException>(() => Mix.Style().Motion("warp"));
    }
}
