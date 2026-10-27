using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// The Marketplace for a brain's scripts: the gallery grouped Built-in / Mine / From others, and
// Get, which installs through AppInstaller and answers a refusal as a message instead of throwing.
// A plain neuron (06-08); the apps it reads and installs are reached through the silo's IIntoChatOS and AppInstaller.
[DigitalBrain.Neuron("apps.store")]
internal sealed class AppStoreNeuron(MarketplaceView marketplace, AppInstaller installer, ILogger<AppStoreNeuron> logger) : DigitalBrain.Neuron, IAppStore
{
    private string Scope => Id.Brain.Value;

    public async Task<IReadOnlyList<GalleryEntry>> List()
    {
        // Only the brain's own callers see its gallery, as only they may Get from it.
        if (BrainScope.CurrentId() != Scope) { return []; }
        var account = CallerContextStamper.TryGet(out var caller) ? caller.AccountId : null;
        var listings = await marketplace.List(default);
        var installed = await InstalledApps.Read(Context.Services.GetRequiredService<IIntoChatOS>(), Scope, default);
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
        try { offers = await Context.Services.GetRequiredService<IIntoChatOS>().Get<IConnectionRequests>(IConnectionRequests.Key(Scope)).ListOffers(); }
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
            && ShippedApps.Is(await Context.Services.GetRequiredService<IIntoChatOS>().Get<IApp>(caller.AppId).Read(), "desktop");
    }

    // Scripts install only apps that ask for nothing: one with requirements is installed from Apps, where the person sees them.
    public async Task<InstallOutcome> Get(string publisher, string name)
    {
        return await Answer(async () =>
        {
            if (BrainScope.CurrentId() != Scope) { return InstallOutcome.Refused("Apps install only into your own brain."); }
            if (!await MayInstall()) { return InstallOutcome.Refused("Only you or the desktop can install apps."); }
            if (await marketplace.Read(publisher, name, default) is { } listing && !AppConsent.IsEmpty(listing.Requires))
            { return InstallOutcome.Refused(AppInstaller.ReviewInApps); }
            var outcome = await installer.Install(new(new(publisher, name), AppConsent.Of(null), Guid.Empty), default);
            return outcome.Status == InstallStatus.NeedsConsent ? InstallOutcome.Refused(AppInstaller.ReviewInApps) : outcome;
        });
    }

    public async Task<InstallOutcome> Install(InstallRequest request)
    {
        return await Answer(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (BrainScope.CurrentId() != Scope) { return InstallOutcome.Refused("Apps install only into your own brain."); }
            if (!await MayInstall()) { return InstallOutcome.Refused("Only you or the desktop can install apps."); }
            return await installer.Install(request, default);
        });
    }

    public async Task<InstallOutcome> Update(UpdateRequest request)
    {
        return await Answer(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (BrainScope.CurrentId() != Scope) { return InstallOutcome.Refused("Apps update only in your own brain."); }
            if (!await MayInstall()) { return InstallOutcome.Refused("Only you or the desktop can update apps."); }
            return await installer.Update(request, default);
        });
    }

    public async Task<InstallOutcome> Remove(string name)
    {
        return await Answer(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (BrainScope.CurrentId() != Scope || !CallerContextStamper.TryGet(out var caller)
                || caller.Kind != CallerKind.User || caller.StampedBy == TrustedEdge.AppProxy)
            { return InstallOutcome.Refused("Only you can remove apps."); }
            if (ShippedApps.Roots.Contains(name)) { return InstallOutcome.Refused($"{name} is part of IntoChat and stays installed."); }
            var app = App(IApp.Key(Scope, name));
            if ((await app.Read()).Manifest.Title.Length == 0) { return InstallOutcome.Refused($"No app named {name} exists in this brain."); }
            await app.Delete();
            return InstallOutcome.Installed(name);
        });
    }

    // One app as the Apps detail shows it: the release it names (the latest listing or one exact release), what that
    // release asks for, and the copy this brain holds of it. A release that can no longer be read is unavailable.
    public async Task<AppDetail?> Describe(AppRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (BrainScope.CurrentId() != Scope) { return null; }
        bool exact;
        try { exact = reference.IsExact(); }
        catch (ArgumentException) { return null; }
        var listing = await marketplace.Read(reference.Publisher, reference.App, default);
        var bundle = await Release(async () =>
        {
            if (exact && listing?.Source is AppSource.Brain published)
            {
                var release = await App(IApp.Key(published.Scope()!, reference.App)).ExportRelease(reference.Version!, reference.Digest!);
                return release.Version == reference.Version && AppPublicationDigest.Of(release) == reference.Digest ? release : null;
            }
            return !exact && listing is not null ? await installer.Bundle(listing.Source, listing.Publisher, reference.App, default) : null;
        });
        var local = await App(IApp.Key(Scope, reference.App)).Read();
        var installed = local.Manifest.Title.Length > 0 && local.Origin is { } origin
            && origin.Publisher == reference.Publisher && origin.Name == reference.App ? local : null;
        if (bundle is null && installed is null) { return null; }
        var manifest = bundle?.Manifest ?? installed!.Manifest;
        var status = bundle is null ? "unavailable"
            : installed is null ? "available"
            : !installed.Edited && listing is not null && AppVersions.Compare(listing.Version, installed.Origin!.Version) > 0 ? "update"
            : "installed";
        return new(reference.Publisher, reference.App, manifest.Title, manifest.Description, manifest.Icon, listing?.Version,
            installed?.Name, installed?.Version, installed?.Edited ?? false, manifest.Requires ?? new AppRequirements(),
            installed is null ? null : installed.Origin!.Consented ?? installed.Manifest.Requires ?? new AppRequirements(),
            manifest.Slots, status, bundle is null ? "This release is no longer available." : null);
    }

    // A refusal is answered as a message, not thrown, as Get always has.
    private static async Task<InstallOutcome> Answer(Func<Task<InstallOutcome>> act)
    {
        try { return await act(); }
        catch (InvalidOperationException error) { return InstallOutcome.Refused(error.Message); }
        catch (KeyNotFoundException error) { return InstallOutcome.Refused(error.Message); }
        catch (UnauthorizedAccessException error) { return InstallOutcome.Refused(error.Message); }
        catch (ArgumentException error) { return InstallOutcome.Refused(error.Message); }
    }

    private static async Task<AppBundle?> Release(Func<Task<AppBundle?>> read)
    {
        try { return await read(); }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private IApp App(string key) => Context.Services.GetRequiredService<IIntoChatOS>().Get<IApp>(key);
}
