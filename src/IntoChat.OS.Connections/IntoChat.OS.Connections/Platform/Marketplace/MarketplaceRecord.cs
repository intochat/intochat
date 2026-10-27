using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Marketplace;

// A Marketplace listing as the trust cluster holds it: the Apps module's listing with its source brain
// spelled out (R54). Shipped listings never reach the trust catalog (R56), so both source fields are set there.
[GenerateSerializer, Alias("platform.marketplace.record")]
public sealed record MarketplaceRecord(
    [property: Id(0)] string Publisher,
    [property: Id(1)] string? SourceAccount,
    [property: Id(2)] string? SourceBrain,
    [property: Id(3)] string Name,
    [property: Id(4)] string Title,
    [property: Id(5)] string Description,
    [property: Id(6)] string Version,
    [property: Id(7)] string? Icon,
    // Phase F: the listing's AppRequirements, spelled out (Framework does not know the Apps contracts). Additive.
    [property: Id(8)] string[]? RequiresSemanticTypes = null,
    [property: Id(9)] string[]? RequiresConnectors = null,
    [property: Id(10)] string[]? RequiresSecrets = null,
    // 0.7.0: the capability ids ("storage@1") the listing requires. Additive.
    [property: Id(11)] string[]? RequiresCapabilities = null);
