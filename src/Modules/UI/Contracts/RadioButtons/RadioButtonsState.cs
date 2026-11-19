namespace IntoChat.OS.Modules.UI.RadioButtons;

[GenerateSerializer, Alias("ui.radio-buttons.option")]
public sealed record RadioOption(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label)
{
    public RadioOption() : this("", "") { }
}

[GenerateSerializer, Alias("ui.radio-buttons.state")]
public sealed record RadioButtonsState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Header,
    [property: Id(2)] IReadOnlyList<RadioOption> Options,
    [property: Id(3)] string? SelectedId,
    [property: Id(4)] bool IsEnabled)
{
    public RadioButtonsState() : this(0, "", [], null, true) { }
}
