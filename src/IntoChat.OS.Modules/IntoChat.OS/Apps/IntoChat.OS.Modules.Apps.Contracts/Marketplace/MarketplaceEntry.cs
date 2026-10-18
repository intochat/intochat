namespace IntoChat.OS.Apps;

// A listing as one brain sees it: whether the brain has it, and whether a newer published
// version is waiting for an unedited copy.
public sealed record MarketplaceEntry(MarketplaceListing Listing, bool Installed, bool UpdateAvailable);
