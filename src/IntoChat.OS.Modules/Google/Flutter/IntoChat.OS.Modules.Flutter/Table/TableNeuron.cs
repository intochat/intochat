using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Table.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Table;

[GrainType(UIVocabulary.TableType)]
internal sealed class TableNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TableState> store)
    : Neuron<TableState>(store), ITable
{
    public Task Replace(string title, IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Title = title.Trim();
        next.Columns = [.. columns];
        next.Rows = [.. rows.Select(row => row.ToList())];
        return Save(next, new TableChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly]
    public Task<TableState> Read()
    {
        var state = Snapshot;
        state.Name = this.GetPrimaryKeyString();
        return Task.FromResult(state);
    }
}
