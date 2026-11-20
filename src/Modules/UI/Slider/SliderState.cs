namespace IntoChat.OS.Modules.UI.Slider;

[GenerateSerializer, Alias("ui.slider.state")]
public sealed record SliderState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] double Value,
    [property: Id(3)] double Min,
    [property: Id(4)] double Max,
    [property: Id(5)] double Step,
    [property: Id(6)] bool IsEnabled,
    [property: Id(7)] string Unit = "")
{
    public SliderState() : this(0, "", 0, 0, 100, 1, true, "") { }
}
