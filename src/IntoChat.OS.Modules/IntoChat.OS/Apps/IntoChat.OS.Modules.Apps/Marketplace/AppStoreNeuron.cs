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
        return entries;
    }

    private static bool Forked(AppSnapshot app) => app is { Origin: not null, Edited: true };

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
