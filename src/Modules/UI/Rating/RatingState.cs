namespace IntoChat.OS.Modules.UI.Rating;

[GenerateSerializer, Alias("ui.rating.state")]
public sealed record RatingState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] int Value,
    [property: Id(3)] int Max,
    [property: Id(4)] bool IsEnabled)
{
    public RatingState() : this(0, "", 0, 5, true) { }
}
