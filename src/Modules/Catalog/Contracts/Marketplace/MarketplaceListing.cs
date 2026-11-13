namespace IntoChat.OS.Modules.Apps;

// One published app as everyone sees it. Publisher is the publishing account's username; Source
// addresses the bundle Install reads. Requires is what installing it asks consent for.
public sealed record MarketplaceListing(
    string Publisher,
    AppSource Source,
    string Name,
    string Title,
    string Description,
    string Version,
    string? Icon,
    AppRequirements? Requires = null);
