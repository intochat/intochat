using DigitalBrain;
using IntoChat.OS;
namespace IntoChat.OS.Modules.Flutter.Collection;

[GenerateSerializer, Alias("ui.collection-item")]
public sealed record CollectionItem(
    [property: Id(0)] string Id, [property: Id(1)] string Label,
    [property: Id(2)] string Kind, [property: Id(3)] string? SecondaryText = null,
    [property: Id(4)] long? SizeBytes = null, [property: Id(5)] DateTimeOffset? ModifiedAt = null,
    [property: Id(6)] bool CanActivate = true, [property: Id(7)] string? TrailingText = null);

[Signal("ui.collection-activated")]
public sealed record CollectionActivated(string Name, string ItemId, long Revision) : DigitalBrain.Signal;
