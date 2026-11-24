using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Units;
using Microsoft.Extensions.Options;
using Idn = DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Apps;

// The one way an app crosses brains: a listed bundle goes into the caller's brain through the
// feature loader. A person's bundle is read through ExportPublished, which is public; a feature
// comes from the shipped catalog. An app that reads data installs only for the brain's owner.
public sealed class AppInstaller(IServiceProvider plain, MarketplaceView marketplace, FeatureCatalog catalog, FeatureLoader loader,
    ICapabilitySatisfaction capabilities, ICapabilityBroker? directory = null)
{
    // The caller the edge stamped for this install.
    internal DigitalBrain.Access.Caller? Caller => (plain.GetService(typeof(DigitalBrain.Access.ICallerAmbient)) as DigitalBrain.Access.ICallerAmbient)?.Current;


    public const string OwnerOnly = "Only the brain owner can install apps that read your data.";
    public const string ReviewInApps = "Open Apps to review what this app needs.";

    // The one install path: the store, an exact release (Circle fork and bring), and the OS's own required apps.
    // Nothing changes unless the consent is the digest of the requirements the bundle declares now, every binding
    // names a connection of its slot's kind, and an app that reads data is installed by the brain's owner. Then the
    // copy lands with its consent recorded in its origin, every required type is granted and the bindings are
    // configured. A source pins an exact release to the publication the caller selected.
    public async Task<AppInstallOutcome> Install(AppInstallRequest request, CancellationToken ct, AppSource.Brain? source = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Ref);
        var reference = request.Ref;
        var exact = reference.IsExact();
        var scope = BrainScope.CurrentId();
        var name = request.Name ?? reference.App;
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
        { throw new ArgumentException("Choose an app name using lowercase letters, digits and dashes."); }
        if (!exact && name != reference.App) { throw new ArgumentException("The latest release installs under its own name."); }
        if (exact && request.OperationId == Guid.Empty) { throw new ArgumentException("An exact release installs with a nonempty operation ID."); }
        var caller = Caller;

        AppBundle bundle;
        AppOrigin origin;
        AppInstallReceipt? receipt = null;
        if (exact)
        {
            if (caller?.For?.Account is not { } account) { throw new UnauthorizedAccessException("Sign in to fork an app."); }
            var listing = await marketplace.Read(reference.Publisher, reference.App, ct);
            if (listing?.Source is not AppSource.Brain published || (source is not null && published != source))
            { throw new KeyNotFoundException("This publication's source is unavailable."); }
            bundle = await SiloNeurons.Get<IApp>(plain, IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(published.Scope()!), reference.App).Key).ExportRelease(reference.Version!, reference.Digest!).WaitAsync(ct);
            var release = PublishedRelease.FromBundle(bundle, reference.Publisher, published, DateTimeOffset.UtcNow);
            release.EnsureMatches(reference.Version!, reference.Digest!);
            // The publication (fork) path: refused before loader.Create makes the copy.
            await CheckCapabilities(scope, bundle.Manifest, ct);
            receipt = release.CreateInstallReceipt(request.OperationId, name, account);
            origin = release.CreateOrigin(account);
        }
        else
        {
            await catalog.Reload(reference.Publisher, reference.App).WaitAsync(ct);
            var listing = await marketplace.Read(reference.Publisher, reference.App, ct)
                ?? throw new KeyNotFoundException($"{reference.Publisher}/{reference.App} is not in the Marketplace.");
            if (listing.Source.Scope() == scope) { throw new InvalidOperationException($"{name} is already yours: you published it."); }
            if ((await SiloNeurons.Get<IApp>(plain, IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), name).Key).Read().WaitAsync(ct)).Manifest.Title.Length > 0)
            { throw new InvalidOperationException($"You already have an app named {name}."); }
            bundle = await Bundle(listing.Source, listing.Publisher, reference.App, ct);
            origin = new(listing.Publisher, listing.Source, reference.App, bundle.Version);
            await CheckCapabilities(scope, bundle.Manifest, ct);
        }

        var requires = bundle.Manifest.Requires ?? new AppRequirements();
        if (request.Consent != AppConsent.Of(requires)) { return AppInstallOutcome.NeedsConsent(requires); }
        if (await Unbound(scope, bundle.Manifest, request.Bindings, ct) is { } refusal) { return AppInstallOutcome.Refused(refusal); }
        if ((requires.SemanticTypes is { Length: > 0 } || request.Bindings is { Count: > 0 }) && !await IsOwner(scope, caller))
        { return AppInstallOutcome.Refused(OwnerOnly); }

        // A retried exact install resumes a copy that exists already; only a copy this call created is rolled back.
        var key = IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), name).Key;
        var existed = exact && (await SiloNeurons.Get<IApp>(plain, key).Read().WaitAsync(ct)).Manifest.Title.Length > 0;
        var consented = origin with { Consented = Recorded(requires) };
        _ = exact
            ? await loader.Create(scope, name, bundle, consented, receipt!, ct)
            : await loader.Load(scope, name, bundle, consented, ct);
        try
        {
            await Grant(key, requires.SemanticTypes, caller, ct);
            if (request.Bindings is { Count: > 0 } bindings)
            { await SiloNeurons.Get<IApp>(plain, key).Configure([], new(bindings, StringComparer.Ordinal)).WaitAsync(ct); }
        }
        catch
        {
            // No half-granted state: the copy and whatever was granted to it go, and the person sees the failure.
            if (!existed) { await RollBack(key, requires.SemanticTypes, caller); }
            throw;
        }
        return AppInstallOutcome.Installed(name);
    }

    // A bundle a person hands over directly (POST /apps/import), under the checks an install has: the stamped caller is a
    // person, a root app is never replaced, and what the bundle asks for is consented to by its digest. No consent sent is
    // consent to nothing, so a bundle that asks for nothing imports as it always did. A bundle that reads data imports only
    // for the owner of the brain. Nothing is granted here: a grant stays an act of the install or of the person.
    public async Task<AppInstallOutcome> Import(AppBundle bundle, string? consent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle.Name);
        if (bundle.Manifest is null) { throw new InvalidDataException("App manifest is required."); }
        var scope = RequirePerson("Only you can import apps.");
        if (ShippedApps.Roots.Contains(bundle.Name)) { throw new UnauthorizedAccessException($"{bundle.Name} is part of IntoChat and is not replaced."); }
        var requires = bundle.Manifest.Requires ?? new AppRequirements();
        if ((consent ?? AppConsent.Of(null)) != AppConsent.Of(requires)) { return AppInstallOutcome.NeedsConsent(requires); }
        if (requires.SemanticTypes is { Length: > 0 } && !await IsOwner(scope, Caller)) { return AppInstallOutcome.Refused(OwnerOnly); }
        await SiloNeurons.Get<IApp>(plain, IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), bundle.Name).Key).Import(bundle).WaitAsync(ct);
        return AppInstallOutcome.Installed(bundle.Name);
    }

    // The one uninstall path, for DELETE /apps/{name} and IAppStore.Remove alike: the stamped caller is a person and a root
    // app stays. False when no app of that name exists in the brain: nothing was removed.
    public async Task<bool> Remove(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var scope = RequirePerson("Only you can remove apps.");
        if (ShippedApps.Roots.Contains(name)) { throw new UnauthorizedAccessException($"{name} is part of IntoChat and stays installed."); }
        var app = SiloNeurons.Get<IApp>(plain, IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), name).Key);
        if ((await app.Read().WaitAsync(ct)).Manifest.Title.Length == 0) { return false; }
        await app.Delete().WaitAsync(ct);
        return true;
    }

    // The brain of the stamped caller when that caller is a person; an app, an agent, a guest or no caller is refused.
    private string RequirePerson(string refusal)
        => Caller is { Actor: DigitalBrain.Access.Actor.Person } caller ? caller.Brain.Value : throw new UnauthorizedAccessException(refusal);

    // Every capability the app requires must be provided by a connected connection in this brain, before any state is
    // created. A requirement is a need, never a grant: this only reads connections and grants nothing.
    internal async Task CheckCapabilities(string scope, AppManifest manifest, CancellationToken ct)
    {
        foreach (var capability in manifest.Requires?.Capabilities ?? [])
        {
            if (!await capabilities.Satisfied(scope, capability, ct))
            { throw new InvalidOperationException($"{manifest.Title} needs {capability}, which no connection in this brain provides."); }
        }
    }

    // An app that asks for nothing records no consent, as every copy installed before consent was asked does.
    private static AppRequirements? Recorded(AppRequirements requires) => AppConsent.IsEmpty(requires) ? null : requires;

    // Each binding names a slot the manifest declares and one of this brain's live connections of that slot's connector.
    internal async Task<string?> Unbound(string scope, AppManifest manifest, Dictionary<string, string>? bindings, CancellationToken ct)
    {
        if (bindings is not { Count: > 0 }) { return null; }
        var connections = await SiloNeurons.Get<IConnectionRequests>(plain, IConnectionRequests.Key(scope)).ListConnections().WaitAsync(ct);
        foreach (var (slotName, id) in bindings)
        {
            if (manifest.Slots.FirstOrDefault(slot => slot.Name == slotName) is not { } slot)
            { return $"This app has no connection named {slotName}."; }
            if (!connections.Any(connection => connection.Id == id && connection.Status == "connected"
                && (slot.Capability is { } capability ? AppCapabilities.Satisfies(connection.Provides, capability) : connection.Kind == slot.Connector)))
            { return $"Choose a {slot.Binds} connection for {slot.Name}."; }
        }
        return null;
    }

    // A person, not an app or a script, who owns this brain: its Owner member in the directory.
    internal async Task<bool> IsOwner(string scope, DigitalBrain.Access.Caller? caller)
    {
        return directory is not null && caller?.Actor is DigitalBrain.Access.Actor.Person person
            && caller.Brain.Value == scope
            && (await (await directory.Identity()).Membership(scope, person.Principal))?.Role == Idn.MemberRole.Owner;
    }

    // Grants are revoked under the stamped caller that granted them (still ambient for this install).
    private async Task RollBack(string appKey, string[]? types, DigitalBrain.Access.Caller? caller)
    {
        if (directory is not null && caller is not null)
        {
            var grants = await directory.Grants();
            foreach (var type in types ?? [])
            {
                try { await grants.RevokeAsOwner(appKey, type, Idn.GrantMode.Always); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
        await SiloNeurons.Get<IApp>(plain, appKey).Delete();
    }

    // Installing is the one consent the install view asks for: the owner grants the app every semantic type it reads, always.
    // Grants read the stamped caller: the installing person must own the brain the install names.
    internal async Task Grant(string appKey, string[]? types, DigitalBrain.Access.Caller? caller, CancellationToken ct)
    {
        if (types is not { Length: > 0 }) { return; }
        if (caller is null) { throw new UnauthorizedAccessException(OwnerOnly); }
        if (plain?.GetService(typeof(IIdentityClient)) is IIdentityClient identityClient)
        {
            var brain = caller.Brain;
            foreach (var type in types)
            {
                await identityClient.GrantAsOwner(brain.Value, Idn.Grant.Tuple($"brain:{brain.Value}", $"app:{appKey}", IntoChat.OS.Host.Primitives.AppRef.Of(brain, appKey).Key, type));
            }
            return;
        }
        if (directory is null) { throw new UnauthorizedAccessException(OwnerOnly); }
        var grants = await directory.Grants(ct);
        foreach (var type in types)
        {
            await grants.GrantAsOwner(new Idn.Grant
            {
                AppId = appKey,
                SemanticTypeId = type,
                Mode = Idn.GrantMode.Always,
                WorkspaceId = caller.Brain.Value,
            }, ct);
        }
    }

    // An update applies as before while it asks for nothing new. One that adds a requirement (to what the person
    // consented to, or to the installed manifest for a copy recorded before consent was) changes nothing until the
    // consent is the digest of the new release's requirements; then only the added types are granted.
    public async Task<AppInstallOutcome> Update(UpdateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name;
        var scope = BrainScope.CurrentId();
        var key = IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), name).Key;
        var target = SiloNeurons.Get<IApp>(plain, key);
        var current = await target.Read().WaitAsync(ct);
        if (current.Origin is not { } origin)
        { throw new InvalidOperationException($"{name} was not installed from the Marketplace."); }
        if (current.Edited)
        { throw new InvalidOperationException($"{name} has your own edits; it no longer takes updates from {origin.Publisher}."); }
        if (origin.Source is AppSource.Shipped) { await catalog.Reload(origin.Publisher, origin.Name).WaitAsync(ct); }
        var listing = await marketplace.Read(origin.Publisher, origin.Name, ct) ?? throw Gone(origin.Source, origin.Publisher, origin.Name);
        if (AppVersions.Compare(listing.Version, origin.Version) <= 0 && !Unconfirmed(current.Status)) { return AppInstallOutcome.Installed(name); }
        var bundle = await Bundle(origin.Source, origin.Publisher, origin.Name, ct);
        // The installed version stays when the new one needs a capability this brain has no connection for.
        await CheckCapabilities(scope, bundle.Manifest, ct);
        var requires = bundle.Manifest.Requires ?? new AppRequirements();
        var added = AppConsent.Added(origin.Consented ?? current.Manifest.Requires, requires);
        var next = origin with { PublicationDigest = null };
        if (AppConsent.IsEmpty(added))
        {
            // Asking for no more: consent follows the release, so a release that asks for less narrows it.
            await loader.Load(scope, name, bundle, next with { Consented = origin.Consented is null ? null : Recorded(requires) }, ct);
            return AppInstallOutcome.Installed(name);
        }
        if (request.Consent != AppConsent.Of(requires)) { return AppInstallOutcome.NeedsConsent(added, requires); }
        var caller = Caller;
        if (added.SemanticTypes is { Length: > 0 } && !await IsOwner(scope, caller)) { return AppInstallOutcome.Refused(OwnerOnly); }
        await loader.Load(scope, name, bundle, next with { Consented = Recorded(requires) }, ct);
        await Grant(key, added.SemanticTypes, caller, ct);
        return AppInstallOutcome.Installed(name);
    }

    public async Task<MarketplaceEntry> Entry(MarketplaceListing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var scope = BrainScope.CurrentId();
        if (listing.Source.Scope() == scope) { return new(listing, true, false); }
        var local = await SiloNeurons.Get<IApp>(plain, IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(scope), listing.Name).Key).Read().WaitAsync(ct);
        return CatalogEntry.FromListing(listing, local, scope).ToMarketplaceEntry();
    }

    // The published bundle a source holds: a person's brain or the shipped catalog.
    public async Task<AppBundle> Bundle(AppSource source, string publisher, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            AppSource.Brain => await SiloNeurons.Get<IApp>(plain, IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(source.Scope()!), name).Key).ExportPublished().WaitAsync(ct),
            AppSource.Shipped => await catalog.Find(publisher, name) ?? throw Gone(source, publisher, name),
            _ => throw new NotSupportedException($"Unknown app source {source.GetType().Name}."),
        };
    }

    // A deploy that failed or never answered: the held program is loaded again until it is Live.
    internal static bool Unconfirmed(AppStatus status) => status is AppStatus.Deploying or AppStatus.Failed;

    private static Exception Gone(AppSource source, string publisher, string name) => source is AppSource.Shipped
        ? new InvalidOperationException($"{publisher}/{name} no longer ships.")
        : new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
