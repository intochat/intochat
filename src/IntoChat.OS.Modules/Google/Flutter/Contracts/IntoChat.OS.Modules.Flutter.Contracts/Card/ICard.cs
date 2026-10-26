using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Card;

[NeuronAlias("card"), DefaultNeuronType(UIVocabulary.CardType)]
public interface ICard
{
    Task Set(string title, string body, IReadOnlyList<UiChildRef>? children = null);
    [DigitalBrain.ReadOnlyCall] Task<CardState> Read();
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
