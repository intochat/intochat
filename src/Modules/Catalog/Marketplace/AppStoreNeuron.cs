using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Units;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Apps;

// The Marketplace for a brain's scripts: the gallery grouped Built-in / Mine / From others, and
// Get, which installs through AppInstaller and answers a refusal as a message instead of throwing.
// A plain neuron (06-08); the apps it reads and installs are reached through the silo's INeuronProxies and AppInstaller.
[DigitalBrain.Neurons.Neuron("apps.store")]
internal sealed class AppStoreNeuron(
    MarketplaceView marketplace,
    AppInstaller installer,
    ILogger<AppStoreNeuron> logger,
    INeuronProxies proxies,
    IBrainObserver observer) : DigitalBrain.Neurons.Neuron, IAppStore
{
    private string Scope => Id.Brain.Value;

    public async Task<IReadOnlyList<GalleryEntry>> List()
    {
        // Only the brain's own callers see its gallery, as only they may Get from it.
        if (BrainScope.CurrentId() != Scope) { return []; }
        var account = Context.Caller?.For?.Account;
        var listings = await marketplace.List(default);
        var installed = await InstalledApps.Read(proxies, observer, Scope, default);
        var held = installed.ToDictionary(app => app.Name, app => app.Snapshot, StringComparer.Ordinal);
        var entries = new List<GalleryEntry>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            held.TryGetValue(listing.Name, out var local);
            var entry = CatalogEntry.FromListing(listing, local, Scope);
            if (entry.IsInstalled) { shown.Add(listing.Name); }
            entries.Add(entry.ToGalleryEntry(account));
        }
        // What the brain holds that no listing shows. An app neuron that was never created answers
        // an empty manifest title, so those are skipped. Own apps and edited copies are Mine.
        foreach (var (name, app) in installed.Where(app => app.Snapshot.Manifest.Title.Length > 0 && !shown.Contains(app.Name)))
        {
            var entry = CatalogEntry.FromInstalled(name, app, Scope, listings);
            entries.Add(entry.ToGalleryEntry(account));
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
        try { offers = await proxies.Get<IConnectionRequests>(IConnectionRequests.Key(Scope)).ListOffers(); }
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

    // The person, or the desktop the OS ships (its gallery), installs; another app may read the gallery but not install from it.
    private async Task<bool> MayInstall()
    {
        var caller = Context.Caller;
        if (caller is null) { return false; }
        if (caller.Actor is Actor.Person) { return true; }
        return caller.Actor is Actor.App app && app.Key == IntoChat.OS.Primitives.AppRef.Of(Id.Brain, "desktop").Key
            && ShippedApps.Is(await proxies.Get<IApp>(app.Key).Read(), "desktop");
    }

    // Scripts install only apps that ask for nothing: one with requirements is installed from Apps, where the person sees them.
    public async Task<AppInstallOutcome> Get(string publisher, string name)
    {
        return await Answer(async () =>
        {
            if (BrainScope.CurrentId() != Scope) { return AppInstallOutcome.Refused("Apps install only into your own brain."); }
            if (!await MayInstall()) { return AppInstallOutcome.Refused("Only you or the desktop can install apps."); }
            if (await marketplace.Read(publisher, name, default) is { } listing && !AppConsent.IsEmpty(listing.Requires))
            { return AppInstallOutcome.Refused(AppInstaller.ReviewInApps); }
            var outcome = await installer.Install(new(new(publisher, name), AppConsent.Of(null), Guid.Empty), default);
            return outcome.Status == AppInstallStatus.NeedsConsent ? AppInstallOutcome.Refused(AppInstaller.ReviewInApps) : outcome;
        });
    }

    public async Task<AppInstallOutcome> Install(AppInstallRequest request)
    {
        return await Answer(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (BrainScope.CurrentId() != Scope) { return AppInstallOutcome.Refused("Apps install only into your own brain."); }
            if (!await MayInstall()) { return AppInstallOutcome.Refused("Only you or the desktop can install apps."); }
            return await installer.Install(request, default);
        });
    }

    public async Task<AppInstallOutcome> Update(UpdateRequest request)
    {
        return await Answer(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (BrainScope.CurrentId() != Scope) { return AppInstallOutcome.Refused("Apps update only in your own brain."); }
            if (!await MayInstall()) { return AppInstallOutcome.Refused("Only you or the desktop can update apps."); }
            return await installer.Update(request, default);
        });
    }

    public async Task<AppInstallOutcome> Remove(string name)
    {
        return await Answer(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (Context.Caller is not { Actor: DigitalBrain.Access.Actor.Person } caller || caller.Brain.Value != Scope)
            { return AppInstallOutcome.Refused("Only you can remove apps."); }
            if (ShippedApps.Roots.Contains(name)) { return AppInstallOutcome.Refused($"{name} is part of IntoChat and stays installed."); }
            var app = App(IntoChat.OS.Primitives.AppRef.Of(Id.Brain, name).Key);
            if ((await app.Read()).Manifest.Title.Length == 0) { return AppInstallOutcome.Refused($"No app named {name} exists in this brain."); }
            await app.Delete();
            return AppInstallOutcome.Installed(name);
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
                var release = await App(IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(published.Scope()!), reference.App).Key).ExportRelease(reference.Version!, reference.Digest!);
                return release.Version == reference.Version && AppPublicationDigest.Of(release) == reference.Digest ? release : null;
            }
            return !exact && listing is not null ? await installer.Bundle(listing.Source, listing.Publisher, reference.App, default) : null;
        });
        var local = await App(IntoChat.OS.Primitives.AppRef.Of(Id.Brain, reference.App).Key).Read();
        var installed = local.Manifest.Title.Length > 0 && local.Origin is { } origin
            && origin.Publisher == reference.Publisher && origin.Name == reference.App ? local : null;
        if (bundle is null && installed is null) { return null; }
        var entry = CatalogEntry.FromDetail(reference, listing, bundle, local);
        return entry.ToAppDetail(bundle is not null);
    }

    // A refusal is answered as a message, not thrown, as Get always has.
    private static async Task<AppInstallOutcome> Answer(Func<Task<AppInstallOutcome>> act)
    {
        try { return await act(); }
        catch (InvalidOperationException error) { return AppInstallOutcome.Refused(error.Message); }
        catch (KeyNotFoundException error) { return AppInstallOutcome.Refused(error.Message); }
        catch (UnauthorizedAccessException error) { return AppInstallOutcome.Refused(error.Message); }
        catch (ArgumentException error) { return AppInstallOutcome.Refused(error.Message); }
    }

    private static async Task<AppBundle?> Release(Func<Task<AppBundle?>> read)
    {
        try { return await read(); }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private IApp App(string key) => proxies.Get<IApp>(key);
}
