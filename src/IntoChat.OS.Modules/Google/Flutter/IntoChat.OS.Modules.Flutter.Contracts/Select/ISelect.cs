using IntoChat.OS;
using IntoChat.OS.Modules.Flutter.Button;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Flutter.Select;

[Alias("select"), Orleans.Metadata.DefaultGrainType(UIVocabulary.SelectType)]
public interface ISelect : INeuron
{
    Task Set(string label, IReadOnlyList<SelectOption> options, string? selected);
    Task SetVisual(ControlVisual visual);
    Task Choose(string value);
    [ReadOnly, Alias("read")] Task<SelectState> Read();
}

[GenerateSerializer, Alias("ui.select-option")]
public sealed record SelectOption([property: Id(0)] string Id, [property: Id(1)] string Label, [property: Id(2)] bool Enabled = true, [property: Id(3)] ControlVisual? Visual = null);

[GenerateSerializer, Alias("ui.select-state")]
[ExportableState]
public sealed class SelectState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public string Label { get; set; } = "";
    [Id(3)] public SelectOption[] Options { get; set; } = [];
    [Id(4)] public string? Selected { get; set; }
    [Id(5)] public ControlVisual? Visual { get; set; }
}

[GenerateSerializer, Alias("ui.select-changed")]
public sealed record SelectChanged([property: Id(0)] string Name, [property: Id(1)] string Value) : Signal;
