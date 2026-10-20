using IntoChat.OS;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps;

// The Marketplace for a brain's scripts: the gallery grouped Built-in / Mine / From others, and
// Get, which installs through AppInstaller and answers a refusal as a message instead of throwing.
[GrainType("apps.store")]
internal sealed class AppStoreNeuron(MarketplaceView marketplace, AppInstaller installer) : Neuron, IAppStore
{
    private string Scope => BrainKey.ScopeOf(this.GetPrimaryKeyString()) ?? this.GetPrimaryKeyString();

    public async Task<IReadOnlyList<GalleryEntry>> List()
    {
        var account = CallerContextStamper.TryGet(out var caller) ? caller.AccountId : null;
        var listings = await marketplace.List(default);
        var installed = await InstalledApps.Read(ServiceProvider.GetRequiredService<IIntoChatOS>(), Scope, default);
        var entries = new List<GalleryEntry>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            var entry = await installer.Entry(listing, default);
            var group = listing.Source switch
            {
                AppSource.Shipped => "builtin",
                AppSource.Brain brain when brain.AccountId == account => "mine",
                _ => "others",
            };
            if (entry.Installed) { shown.Add(listing.Name); }
            entries.Add(new(group, listing.Publisher, listing.Name, listing.Title, listing.Description, listing.Icon,
                entry.Installed, entry.UpdateAvailable, entry.Installed ? listing.Name : null));
        }
        // What the brain holds that no listing shows: its own apps and edited copies are Mine.
        foreach (var (name, app) in installed.Where(held => held.Snapshot.Manifest.Title.Length > 0 && (!shown.Contains(held.Name) || held.Snapshot is { Origin: not null, Edited: true })))
        {
            var mine = app.Origin is null || app.Edited;
            entries.Add(new(mine ? "mine" : "others", app.Origin?.Publisher ?? "", name, app.Manifest.Title, app.Manifest.Description,
                app.Manifest.Icon, true, InstalledApps.UpdateAvailable(app, listings), name));
        }
        return entries;
    }

    public async Task<InstallOutcome> Get(string publisher, string name)
    {
        try
        {
            if (BrainScope.CurrentId() != Scope) { return new(false, null, "Apps install only into your own brain."); }
            var snapshot = await installer.Install(publisher, name, default);
            return new(true, snapshot.Name, null);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
        { return new(false, null, error.Message); }
    }
}
