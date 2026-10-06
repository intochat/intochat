namespace DigitalBrain.Apps;

// Where an installed copy came from: the publisher's account and brain, its name for the app,
// and the published version the copy was taken at.
[GenerateSerializer, Alias("apps.origin")]
public sealed record AppOrigin(
    [property: Id(0)] string PublisherAccountId,
    [property: Id(1)] string PublisherBrainId,
    [property: Id(2)] string Publisher,
    [property: Id(3)] string Name,
    [property: Id(4)] string Version);
