using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Collection;

[GrainType(UIVocabulary.CollectionType)]
internal sealed class CollectionNeuron(
    [PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CollectionState> store)
    : Neuron, ICollectionView
{
    public async Task Set(CollectionDefinition definition, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Items.Length > 500 || definition.Items.Select(x => x.Id).Distinct().Count() != definition.Items.Length) { throw new ArgumentException("Collection items must have unique IDs and contain at most 500 items."); }
        if (store.State.Revision != expectedRevision) { throw new RevisionConflictException("The UI changed. Read its latest revision before retrying.", store.State.Revision); }
        var previous = store.State;
        store.State = new() { Name = this.GetPrimaryKeyString(), Revision = checked(previous.Revision + 1), Definition = definition };
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        await PublishAsync(new CollectionChanged(this.GetPrimaryKeyString(), store.State.Revision));
    }

    public async Task Select(string itemId, long expectedRevision)
    {
        if (!store.State.Definition.Items.Any(x => x.Id == itemId)) { throw new ArgumentException("Unknown collection item."); }
        await Set(store.State.Definition with { Selection = itemId }, expectedRevision);
    }
    public async Task Activate(string itemId, long expectedRevision)
    {
        if (store.State.Revision != expectedRevision) { throw new RevisionConflictException("The collection changed. Refresh it first.", store.State.Revision); }
        if (!store.State.Definition.Items.Any(x => x.Id == itemId && x.CanActivate) &&
            !(store.State.Definition.Breadcrumbs?.Any(x => x.Id == itemId) ?? false)) { throw new ArgumentException("This item cannot be opened."); }
        await PublishAsync(new CollectionActivated(this.GetPrimaryKeyString(), itemId, expectedRevision));
    }
    public Task<CollectionState> Read() => Task.FromResult(store.State);
}
