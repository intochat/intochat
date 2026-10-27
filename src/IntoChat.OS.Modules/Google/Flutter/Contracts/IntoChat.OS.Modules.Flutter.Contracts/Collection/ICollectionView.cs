using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Collection;

[NeuronAlias("collection"), DefaultNeuronType(UIVocabulary.CollectionType)]
public interface ICollectionView
{
    Task Set(CollectionDefinition definition, long expectedRevision);
    Task Select(string itemId, long expectedRevision);
    Task Activate(string itemId, long expectedRevision);
    [DigitalBrain.ReadOnlyCall] Task<CollectionState> Read();
}

[GenerateSerializer, Alias("ui.collection-definition")]
public sealed record CollectionDefinition([property: Id(0)] CollectionItem[] Items, [property: Id(1)] string? Selection = null, [property: Id(2)] string? Cursor = null, [property: Id(3)] string? Error = null,
    [property: Id(4)] bool FileMode = true, [property: Id(5)] bool SelectionEnabled = true,
    [property: Id(6)] string? EmptyLabel = null, [property: Id(7)] CollectionBreadcrumb[]? Breadcrumbs = null);

[GenerateSerializer, Alias("ui.collection-breadcrumb")]
public sealed record CollectionBreadcrumb([property: Id(0)] string Id, [property: Id(1)] string Label);

[GenerateSerializer, Alias("ui.collection-state")]
[ExportableState]
public sealed class CollectionState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public CollectionDefinition Definition { get; set; } = new([]);
}

[Signal("ui.collection-changed")]
public sealed record CollectionChanged(string Name, long Revision) : DigitalBrain.Signal;
