using DigitalBrain.Contracts;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Space;

// The Space's own data: a person's profile and posts, and a posts list, as plain records. They kept the JSON shape of the
// widget states they were (PersonState, CardState, CollectionState), so a stored record still deserialises.
[ExportableState]
public sealed class PersonState
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string DisplayName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
}

[ExportableState]
public sealed class CardState
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public ViewRef[] Children { get; set; } = [];
}

[ExportableState]
public sealed class CollectionState
{
    public string Name { get; set; } = "";
    public long Revision { get; set; }
    public CollectionDefinition Definition { get; set; } = new([]);
}

public sealed record CollectionDefinition(CollectionItem[] Items, string? Selection = null, string? Cursor = null, string? Error = null,
    bool FileMode = true, bool SelectionEnabled = true,
    string? EmptyLabel = null, CollectionBreadcrumb[]? Breadcrumbs = null);

public sealed record CollectionBreadcrumb(string Id, string Label);

public sealed record CollectionItem(
    string Id, string Label,
    string Kind, string? SecondaryText = null,
    long? SizeBytes = null, DateTimeOffset? ModifiedAt = null,
    bool CanActivate = true, string? TrailingText = null);

// What a Space persists: the brain's profile ({brain}/profile) and its posts ({brain}/posts/{id}).
[ExportableState]
public sealed class SpaceRecords
{
    public PersonState Profile { get; set; } = new();
    public CardState[] Posts { get; set; } = [];
}

// A brain's Space records changed: the spaces that show its profile or posts are rebuilt.
[DigitalBrain.Signal("ui.space-records-changed")]
public sealed record SpaceRecordsChanged(string Brain) : DigitalBrain.Signal;

// Module-only reads of another brain's Space records, after the viewer's Space authorized the exact reference.
[NeuronAlias("ui.space-records"), DefaultNeuronType(ISpace.Type)]
internal interface ISpaceRecords
{
    [DigitalBrain.Contracts.ReadOnlyCall] Task<SpaceRecords> Records();
}
