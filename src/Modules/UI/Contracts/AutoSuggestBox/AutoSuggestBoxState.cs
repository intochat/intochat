namespace IntoChat.OS.Modules.UI.AutoSuggestBox;

[GenerateSerializer, Alias("ui.auto-suggest.state")]
public sealed record AutoSuggestBoxState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Placeholder,
    [property: Id(2)] string Query,
    [property: Id(3)] string[] Items,
    [property: Id(4)] string[] Suggestions,
    [property: Id(5)] string? SubmittedText)
{
    public AutoSuggestBoxState() : this(0, "Search…", "", [], [], null) { }
}
