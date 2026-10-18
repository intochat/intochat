using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Text.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Text;

[GrainType(UIVocabulary.TextType)]
internal sealed class TextNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TextState> store)
    : Neuron<TextState>(store), IText
{
    public Task Set(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Markdown = markdown;
        return Save(next, new TextChanged(this.GetPrimaryKeyString()));
    }

    [ReadOnly] public Task<TextState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
