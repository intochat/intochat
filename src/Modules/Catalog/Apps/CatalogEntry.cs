using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.AI.Routing;

namespace IntoChat.OS.Modules.Apps;

internal enum CatalogGroup
{
    Builtin,
    Mine,
    Others
}

/// <summary>
/// Domain model for an application or unit in the catalog, uniting marketplace listings, installed snapshots,
/// and published releases. Encapsulates grouping, status calculation, update detection, and presentation projections.
/// </summary>
internal sealed record CatalogEntry
{
    public string Publisher { get; }
    public AppSource Source { get; }
    public string Name { get; }
    public string Title { get; }
    public string Description { get; }
    public string Version { get; }
    public string? Icon { get; }
    public AppRequirements? Requires { get; }
    public IReadOnlyList<AppOperation> Operations { get; }
    public IReadOnlyList<AppSlot> Slots { get; }
    public AppPresentation? Presentation { get; }

    public AppSnapshot? InstalledSnapshot { get; }
    public bool IsInstalled { get; }
    public string? InstalledVersion { get; }
    public string? InstalledName { get; }
    public bool IsEdited { get; }
    public bool IsForked { get; }
    public bool HasUpdate { get; }
    public AppRequirements? Consented { get; }

    public string DisplayTitle => IsForked ? $"Copy of {Title}" : Title;
    public bool IsRoutable => Operations.Count > 0;
    public bool HasUiPresence => Presentation?.Root is { } root
        && (root.Sphere is not null || root.Main is not null || root.Side is not null || (root.Views?.Length ?? 0) > 0);

    public CatalogEntry(
        string publisher,
        AppSource source,
        string name,
        string title,
        string description,
        string version,
        string? icon = null,
        AppRequirements? requires = null,
        IReadOnlyList<AppOperation>? operations = null,
        IReadOnlyList<AppSlot>? slots = null,
        AppPresentation? presentation = null,
        AppSnapshot? installedSnapshot = null,
        bool isInstalled = false,
        string? installedVersion = null,
        string? installedName = null,
        bool isEdited = false,
        bool isForked = false,
        bool hasUpdate = false,
        AppRequirements? consented = null)
    {
        Publisher = publisher ?? string.Empty;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Title = title ?? string.Empty;
        Description = description ?? string.Empty;
        Version = version ?? "0.0.0";
        Icon = icon;
        Requires = requires;
        Operations = operations ?? [];
        Slots = slots ?? [];
        Presentation = presentation;
        InstalledSnapshot = installedSnapshot;
        IsInstalled = isInstalled;
        InstalledVersion = installedVersion;
        InstalledName = installedName;
        IsEdited = isEdited;
        IsForked = isForked;
        HasUpdate = hasUpdate;
        Consented = consented;
    }

    public static CatalogEntry FromListing(MarketplaceListing listing, AppSnapshot? local = null, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(listing);

        bool isInstalled;
        bool hasUpdate = false;
        bool isEdited = false;
        bool isForked = false;
        string? installedName = null;
        string? installedVersion = null;
        AppRequirements? consented = null;

        if (scope is not null && listing.Source.Scope() == scope)
        {
            isInstalled = true;
            installedName = listing.Name;
            installedVersion = listing.Version;
        }
        else if (local is not null && local.Manifest.Title.Length > 0
            && local.Origin is { } origin
            && string.Equals(origin.Publisher, listing.Publisher, StringComparison.Ordinal)
            && string.Equals(origin.Name, listing.Name, StringComparison.Ordinal))
        {
            isInstalled = true;
            isEdited = local.Edited;
            isForked = local.Origin is not null && local.Edited;
            installedName = local.Name;
            installedVersion = local.Origin?.Version ?? local.Version;
            hasUpdate = !local.Edited && AppVersions.Compare(listing.Version, local.Origin!.Version) > 0;
            consented = local.Origin?.Consented ?? local.Manifest.Requires;
        }
        else
        {
            isInstalled = false;
        }

        var description = (isForked && local?.Manifest is not null) ? local.Manifest.Description : listing.Description;
        var icon = (isForked && local?.Manifest is not null) ? local.Manifest.Icon : listing.Icon;

        return new CatalogEntry(
            publisher: listing.Publisher,
            source: listing.Source,
            name: listing.Name,
            title: listing.Title,
            description: description,
            version: listing.Version,
            icon: icon,
            requires: listing.Requires,
            operations: local?.Manifest?.Operations,
            slots: local?.Manifest?.Slots,
            presentation: local?.Presentation,
            installedSnapshot: local,
            isInstalled: isInstalled,
            installedVersion: installedVersion,
            installedName: installedName,
            isEdited: isEdited,
            isForked: isForked,
            hasUpdate: hasUpdate,
            consented: consented);
    }

    public static CatalogEntry FromInstalled(
        string name,
        AppSnapshot snapshot,
        string scope,
        IReadOnlyList<MarketplaceListing>? listings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(snapshot);

        var manifest = snapshot.Manifest;
        var publisher = snapshot.Origin?.Publisher ?? string.Empty;
        var source = snapshot.Origin?.Source ?? new AppSource.Brain(string.Empty, scope);
        var isForked = snapshot.Origin is not null && snapshot.Edited;
        var hasUpdate = listings is not null && IsUpdateAvailable(snapshot, listings);

        return new CatalogEntry(
            publisher: publisher,
            source: source,
            name: name,
            title: manifest.Title,
            description: manifest.Description,
            version: snapshot.Version,
            icon: manifest.Icon,
            requires: manifest.Requires,
            operations: manifest.Operations,
            slots: manifest.Slots,
            presentation: snapshot.Presentation,
            installedSnapshot: snapshot,
            isInstalled: true,
            installedVersion: snapshot.Version,
            installedName: name,
            isEdited: snapshot.Edited,
            isForked: isForked,
            hasUpdate: hasUpdate,
            consented: snapshot.Origin?.Consented ?? manifest.Requires);
    }

    public static CatalogEntry FromSnapshot(string name, AppSnapshot snapshot, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(snapshot);

        var manifest = snapshot.Manifest ?? new AppManifest(name, "", [], [], []);
        return new CatalogEntry(
            publisher: snapshot.Origin?.Publisher ?? string.Empty,
            source: snapshot.Origin?.Source ?? new AppSource.Brain(string.Empty, scope),
            name: name,
            title: manifest.Title,
            description: manifest.Description,
            version: snapshot.Version,
            icon: manifest.Icon,
            requires: manifest.Requires,
            operations: manifest.Operations,
            slots: manifest.Slots,
            presentation: snapshot.Presentation,
            installedSnapshot: snapshot,
            isInstalled: true,
            installedVersion: snapshot.Version,
            installedName: name,
            isEdited: snapshot.Edited,
            isForked: snapshot.Origin is not null && snapshot.Edited,
            hasUpdate: false,
            consented: snapshot.Origin?.Consented ?? manifest.Requires);
    }

    public static CatalogEntry FromDetail(
        AppRef reference,
        MarketplaceListing? listing,
        AppBundle? bundle,
        AppSnapshot? local)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var installed = local is not null && local.Manifest.Title.Length > 0
            && local.Origin is { } origin
            && string.Equals(origin.Publisher, reference.Publisher, StringComparison.Ordinal)
            && string.Equals(origin.Name, reference.App, StringComparison.Ordinal)
            ? local
            : null;

        var manifest = bundle?.Manifest ?? installed?.Manifest ?? new AppManifest(reference.App, "", [], [], []);
        var publisher = reference.Publisher;
        var name = reference.App;
        var title = manifest.Title;
        var description = manifest.Description;
        var icon = manifest.Icon;
        var version = listing?.Version ?? installed?.Version ?? bundle?.Version ?? "0.0.0";
        var requires = manifest.Requires ?? new AppRequirements();
        var slots = manifest.Slots;
        var isInstalled = installed is not null;
        var isEdited = installed?.Edited ?? false;
        var isForked = installed is not null && installed.Origin is not null && installed.Edited;
        var hasUpdate = isInstalled && !isEdited && listing is not null
            && AppVersions.Compare(listing.Version, installed!.Origin!.Version) > 0;
        var consented = installed is null ? null : installed.Origin!.Consented ?? installed.Manifest.Requires ?? new AppRequirements();
        var source = listing?.Source ?? installed?.Origin?.Source ?? new AppSource.Brain(string.Empty, string.Empty);

        return new CatalogEntry(
            publisher: publisher,
            source: source,
            name: name,
            title: title,
            description: description,
            version: version,
            icon: icon,
            requires: requires,
            operations: manifest.Operations,
            slots: slots,
            presentation: installed?.Presentation,
            installedSnapshot: installed,
            isInstalled: isInstalled,
            installedVersion: installed?.Version,
            installedName: installed?.Name,
            isEdited: isEdited,
            isForked: isForked,
            hasUpdate: hasUpdate,
            consented: consented);
    }

    public CatalogGroup Group(string? currentAccountId)
    {
        if (Source is AppSource.Shipped)
        {
            return CatalogGroup.Builtin;
        }

        if (Source is AppSource.Brain brain && string.Equals(brain.AccountId, currentAccountId, StringComparison.Ordinal))
        {
            return CatalogGroup.Mine;
        }

        if (IsForked)
        {
            return CatalogGroup.Mine;
        }

        if (InstalledSnapshot is not null && (InstalledSnapshot.Origin is null || InstalledSnapshot.Edited))
        {
            return CatalogGroup.Mine;
        }

        return CatalogGroup.Others;
    }

    public string GroupName(string? currentAccountId) => Group(currentAccountId) switch
    {
        CatalogGroup.Builtin => "builtin",
        CatalogGroup.Mine => "mine",
        _ => "others",
    };

    public string DetermineStatus(bool releaseAvailable = true)
    {
        if (!releaseAvailable && !IsInstalled)
        {
            return "unavailable";
        }

        if (!IsInstalled)
        {
            return "available";
        }

        if (HasUpdate)
        {
            return "update";
        }

        return "installed";
    }

    public MarketplaceListing ToMarketplaceListing() =>
        new(Publisher, Source, Name, Title, Description, Version, Icon, Requires);

    public MarketplaceEntry ToMarketplaceEntry() =>
        new(ToMarketplaceListing(), IsInstalled, HasUpdate);

    public GalleryEntry ToGalleryEntry(string? currentAccountId) =>
        new(
            Group: GroupName(currentAccountId),
            Publisher: Publisher,
            Name: Name,
            Title: DisplayTitle,
            Description: Description,
            Icon: Icon,
            Installed: IsInstalled,
            UpdateAvailable: HasUpdate,
            InstalledName: IsInstalled ? (InstalledName ?? Name) : null);

    public AppDetail ToAppDetail(bool releaseAvailable = true, string? reason = null)
    {
        var status = DetermineStatus(releaseAvailable);
        return new AppDetail(
            Publisher: Publisher,
            App: Name,
            Title: Title,
            Description: Description,
            Icon: Icon,
            LatestVersion: Version,
            InstalledName: InstalledName,
            InstalledVersion: InstalledVersion,
            Edited: IsEdited,
            Requires: Requires ?? new AppRequirements(),
            Consented: Consented,
            Slots: [.. Slots],
            Status: status,
            Reason: reason ?? (!releaseAvailable ? "This release is no longer available." : null));
    }

    public IReadOnlyList<MenuOperation> ToMenuOperations() =>
        [.. Operations.Select(op => new MenuOperation(
            Name,
            op.Name,
            string.IsNullOrWhiteSpace(op.Description) ? null : op.Description))];

    public static bool IsUpdateAvailable(AppSnapshot snapshot, IReadOnlyList<MarketplaceListing> listings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(listings);

        return snapshot.Origin is { } source && !snapshot.Edited
            && listings.FirstOrDefault(l => string.Equals(l.Publisher, source.Publisher, StringComparison.Ordinal)
                && string.Equals(l.Name, source.Name, StringComparison.Ordinal)) is { } listed
            && AppVersions.Compare(listed.Version, source.Version) > 0;
    }

    public static string? ResolveOwnerKey(MarketplaceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (listing.Source is AppSource.Brain brain && string.IsNullOrWhiteSpace(brain.AccountId))
        {
            return string.IsNullOrWhiteSpace(brain.BrainId) ? null : IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain.BrainId), listing.Name).Key;
        }
        return listing.Source.Scope() is { } scope ? IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), listing.Name).Key : null;
    }
}
