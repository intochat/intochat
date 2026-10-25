using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Tabs;

[NeuronAlias("tabs"), DefaultNeuronType(UIVocabulary.TabsType)]
public interface ITabs
{
    Task Set(IReadOnlyList<TabItem> tabs, string selectedId);
    Task Select(string id);
    [ReadOnlyCall] Task<TabsState> Read();
}

[GenerateSerializer, Alias("ui.tab-item")]
public sealed record TabItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] UiChildRef Child);

[GenerateSerializer, Alias("ui.tabs-state")]
[ExportableState]
public sealed class TabsState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public List<TabItem> Tabs { get; set; } = [];
    [Id(3)] public string SelectedId { get; set; } = "";
}
