namespace DigitalBrain.Apps;

// One published app as everyone sees it. Publisher is the publishing account's username; the
// account and brain ids address the publisher's copy for Install.
[GenerateSerializer, Alias("apps.listing")]
public sealed record MarketplaceListing(
    [property: Id(0)] string Publisher,
    [property: Id(1)] string PublisherAccountId,
    [property: Id(2)] string PublisherBrainId,
    [property: Id(3)] string Name,
    [property: Id(4)] string Title,
    [property: Id(5)] string Description,
    [property: Id(6)] string Version,
    [property: Id(7)] string? Icon,
    [property: Id(8)] string[] Scenarios);
