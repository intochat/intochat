using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps;

// The Marketplace for a brain's scripts: the gallery grouped Built-in / Mine / From others, and
// Get, which installs through AppInstaller and answers a refusal as a message instead of throwing.
[GrainType("apps.store")]
internal sealed class AppStoreNeuron(MarketplaceView marketplace, AppInstaller installer, ILogger<AppStoreNeuron> logger) : Neuron, IAppStore
{
    private string Scope => BrainKey.ScopeOf(this.GetPrimaryKeyString()) ?? this.GetPrimaryKeyString();

    public async Task<IReadOnlyList<GalleryEntry>> List()
    {
        // Only the brain's own callers see its gallery, as only they may Get from it.
        if (BrainScope.CurrentId() != Scope) { return []; }
        var account = CallerContextStamper.TryGet(out var caller) ? caller.AccountId : null;
        var listings = await marketplace.List(default);
        var installed = await InstalledApps.Read(ServiceProvider.GetRequiredService<IIntoChatOS>(), Scope, default);
        var held = installed.ToDictionary(app => app.Name, app => app.Snapshot, StringComparer.Ordinal);
        var entries = new List<GalleryEntry>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            var entry = await installer.Entry(listing, default);
            if (entry.Installed) { shown.Add(listing.Name); }
            // An edited copy is a fork: it shows once, under Mine, and its listing's card is not shown.
            if (entry.Installed && held.TryGetValue(listing.Name, out var local) && Forked(local))
            {
                entries.Add(new("mine", listing.Publisher, listing.Name, $"Copy of {listing.Title}", local.Manifest.Description,
                    local.Manifest.Icon, true, false, listing.Name));
                continue;
            }
            var group = listing.Source switch
            {
                AppSource.Shipped => "builtin",
                AppSource.Brain brain when brain.AccountId == account => "mine",
                _ => "others",
            };
            entries.Add(new(group, listing.Publisher, listing.Name, listing.Title, listing.Description, listing.Icon,
                entry.Installed, entry.UpdateAvailable, entry.Installed ? listing.Name : null));
        }
        // What the brain holds that no listing shows. An app neuron that was never created answers
        // an empty manifest title, so those are skipped. Own apps and edited copies are Mine.
        foreach (var (name, app) in installed.Where(app => app.Snapshot.Manifest.Title.Length > 0 && !shown.Contains(app.Name)))
        {
            var mine = app.Origin is null || app.Edited;
            var title = Forked(app) ? $"Copy of {app.Manifest.Title}" : app.Manifest.Title;
            entries.Add(new(mine ? "mine" : "others", app.Origin?.Publisher ?? "", name, title, app.Manifest.Description,
                app.Manifest.Icon, true, InstalledApps.UpdateAvailable(app, listings), name));
        }
        entries.AddRange(await Connections());
        return entries;
    }

    // Connectors sit under From others, Gmail and Salesforce first: Installed is connected; one the host has
    // not configured still shows, as not available. A host composing no connector answers none. A failing
    // connection catalog is logged and the gallery still shows its apps.
    private async Task<GalleryEntry[]> Connections()
    {
        ConnectorOffer[] offers;
        try { offers = await ServiceProvider.GetRequiredService<IIntoChatOS>().Get<IConnectionRequests>(Scope).ListOffers(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "The gallery could not read the connection catalog of {Brain}; it shows apps only.", Scope);
            return [];
        }
        return [.. Ordered(offers).Select(offer => new GalleryEntry("others", "", offer.Id, offer.DisplayName,
            offer.Connected ? "Connected." : offer.Available ? "Connect your " + offer.DisplayName + " account." : "Not available on this host.",
            null, offer.Connected, false, null, "connection", offer.Id, offer.Available))];
    }

    private static readonly string[] Featured = ["gmail", "salesforce"];

    internal static IEnumerable<ConnectorOffer> Ordered(IEnumerable<ConnectorOffer> offers)
        => offers.OrderBy(offer => Array.IndexOf(Featured, offer.Id) is var at and >= 0 ? at : Featured.Length)
            .ThenBy(offer => offer.DisplayName, StringComparer.OrdinalIgnoreCase);

    private static bool Forked(AppSnapshot app) => app is { Origin: not null, Edited: true };

    // The person, or the desktop the OS ships (its gallery), installs; another app may read the gallery but not install from it.
    private async Task<bool> MayInstall()
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return false; }
        if (caller.Kind == CallerKind.User) { return true; }
        return caller.Kind == CallerKind.App && caller.AppId == IApp.Key(Scope, "desktop")
            && ShippedApps.Is(await ServiceProvider.GetRequiredService<IIntoChatOS>().Get<IApp>(caller.AppId).Read(), "desktop");
    }

    public async Task<InstallOutcome> Get(string publisher, string name)
    {
        try
        {
            if (BrainScope.CurrentId() != Scope) { return new(false, null, "Apps install only into your own brain."); }
            if (!await MayInstall()) { return new(false, null, "Only you or the desktop can install apps."); }
            var snapshot = await installer.Install(publisher, name, default);
            return new(true, snapshot.Name, null);
        }
        catch (InvalidOperationException error) { return new(false, null, error.Message); }
        catch (KeyNotFoundException error) { return new(false, null, error.Message); }
        catch (UnauthorizedAccessException error) { return new(false, null, error.Message); }
        catch (ArgumentException error) { return new(false, null, error.Message); }
    }
}
