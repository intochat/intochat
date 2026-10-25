using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Card.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Card;

[GrainType(UIVocabulary.CardType)]
internal sealed class CardNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CardState> store)
    : Neuron<CardState>(store), ICard, ISharedCardReader
{
    public Task Set(string title, string body, IReadOnlyList<UiChildRef>? children = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Title = title.Trim();
        next.Body = body;
        next.Children = [.. children ?? []];
        return Save(next, new CardChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly] public Task<CardState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}

// Module-only leaf access after the viewer Space neuron authorizes the exact reference.
[Orleans.Metadata.DefaultGrainType(UIVocabulary.CardType)]
[Alias("ui.shared-card-reader")]
internal interface ISharedCardReader : IGrainWithStringKey
{
    Task<CardState> Read();
}
