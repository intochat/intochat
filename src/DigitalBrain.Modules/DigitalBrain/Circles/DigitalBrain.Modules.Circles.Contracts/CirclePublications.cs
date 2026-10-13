using DigitalBrain.Apps;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.publication-feed")]
public sealed record CirclePublicationFeed(
    [property: Id(0)] AppPublication[] Items,
    [property: Id(1)] string[] UnavailablePublishers,
    [property: Id(2)] string PrincipalId);
