using IntoChat.OS;
using Orleans.Concurrency;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Card;

[Alias("card"), Orleans.Metadata.DefaultGrainType(UIVocabulary.CardType)]
public interface ICard : INeuron
{
    Task Set(string title, string body, IReadOnlyList<UiChildRef>? children = null);
    [ReadOnly, Alias("read")] Task<CardState> Read();
}

[GenerateSerializer, Alias("ui.card-state")]
[ExportableState]
public sealed class CardState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Title { get; set; } = "";
    [Id(3)] public string Body { get; set; } = "";
    [Id(4)] public List<UiChildRef> Children { get; set; } = [];
}
