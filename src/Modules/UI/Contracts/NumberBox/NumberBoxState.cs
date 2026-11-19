namespace IntoChat.OS.Modules.UI.NumberBox;

[GenerateSerializer, Alias("ui.number-box.state")]
public sealed record NumberBoxState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] double Value,
    [property: Id(3)] double Min,
    [property: Id(4)] double Max,
    [property: Id(5)] double Step,
    [property: Id(6)] bool IsEnabled,
    [property: Id(7)] string Prefix = "")
{
    public NumberBoxState() : this(0, "", 0, double.MinValue, double.MaxValue, 1, true, "") { }
}
