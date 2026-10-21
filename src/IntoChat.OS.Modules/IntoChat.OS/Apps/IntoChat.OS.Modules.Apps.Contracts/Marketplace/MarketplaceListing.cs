namespace IntoChat.OS.Apps;

// One published app as everyone sees it. Publisher is the publishing account's username; Source
// addresses the bundle Install reads. Requires is what installing it asks consent for.
[GenerateSerializer, Alias("apps.listing")]
public sealed record MarketplaceListing(
    [property: Id(0)] string Publisher,
    [property: Id(1)] AppSource Source,
    [property: Id(2)] string Name,
    [property: Id(3)] string Title,
    [property: Id(4)] string Description,
    [property: Id(5)] string Version,
    [property: Id(6)] string? Icon,
    [property: Id(7)] AppRequirements? Requires = null);
