using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Sheet.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Sheet;

[GrainType(UIVocabulary.SheetType)]
internal sealed class SheetNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<SheetState> store)
    : Neuron<SheetState>(store), ISheet
{
    public Task Set(string title, IReadOnlyList<SheetCell> cells)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(cells);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Title = title.Trim();
        next.Cells = [.. cells];
        return Save(next, new SheetChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly] public Task<SheetState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
