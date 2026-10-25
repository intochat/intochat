using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Tabs.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Tabs;

[GrainType(UIVocabulary.TabsType)]
internal sealed class TabsNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TabsState> store)
    : Neuron<TabsState>(store), ITabs
{
    public Task Set(IReadOnlyList<TabItem> tabs, string selectedId)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedId);
        if (!tabs.Any(tab => tab.Id == selectedId)) { throw new ArgumentException("Selected tab is not in the set.", nameof(selectedId)); }
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Tabs = [.. tabs];
        next.SelectedId = selectedId;
        return Save(next, new TabsChanged(this.GetPrimaryKeyString(), next.SelectedId));
    }

    public Task Select(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var next = Snapshot;
        if (!next.Tabs.Exists(tab => tab.Id == id)) { throw new ArgumentException("Unknown tab.", nameof(id)); }
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.SelectedId = id;
        return Save(next, new TabsChanged(this.GetPrimaryKeyString(), next.SelectedId));
    }

    [ReadOnly] public Task<TabsState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
