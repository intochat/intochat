using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Integrations;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Apps;

// The one way an app crosses brains: a listed bundle goes into the caller's brain through the
// feature loader. A person's bundle is read through ExportPublished, which is public; a feature
// comes from the features folder. An app that reads data installs only for the brain's owner.
public sealed class AppInstaller(IntoChat.OS.Runtime.IPlainNeurons plain, MarketplaceView marketplace, FeatureCatalog catalog, FeatureLoader loader,
    IEnumerable<IFeatureMigration> migrations, ICapabilitySatisfaction capabilities, IIdentityClient? identity = null,
    IOptions<AuthOptions>? auth = null)
{

    public const string OwnerOnly = "Only the brain owner can install apps that read your data.";
    public const string ReviewInApps = "Open Apps to review what this app needs.";

    // The one install path: the store, an exact release (Circle fork and bring), and the OS's own required apps.
    // Nothing changes unless the consent is the digest of the requirements the bundle declares now, every binding
    // names a connection of its slot's kind, and an app that reads data is installed by the brain's owner. Then the
    // copy lands with its consent recorded in its origin, every required type is granted and the bindings are
    // configured. A source pins an exact release to the publication the caller selected.
    public async Task<InstallOutcome> Install(InstallRequest request, CancellationToken ct, AppSource.Brain? source = null)
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
        var caller = CallerContextStamper.TryGet(out var stamped) ? stamped : null;

        AppBundle bundle;
        AppOrigin origin;
        AppInstallReceipt? receipt = null;
        if (exact)
        {
            if (caller is null) { throw new UnauthorizedAccessException("Sign in to fork an app."); }
            var listing = await marketplace.Read(reference.Publisher, reference.App, ct);
            if (listing?.Source is not AppSource.Brain published || (source is not null && published != source))
            { throw new KeyNotFoundException("This publication's source is unavailable."); }
            bundle = await plain.Get<IApp>(IApp.Key(published.Scope()!, reference.App)).ExportRelease(reference.Version!, reference.Digest!).WaitAsync(ct);
            if (bundle.Version != reference.Version || AppPublicationDigest.Of(bundle) != reference.Digest)
            { throw new InvalidDataException("The published content does not match the selected release."); }
            // The publication (fork) path: refused before loader.Create makes the copy.
            await CheckCapabilities(scope, bundle.Manifest, ct);
            var publication = new AppPublicationRef(reference.Publisher, published, reference.App, reference.Version!, reference.Digest!);
            receipt = new AppInstallReceipt(request.OperationId, publication, name, caller.AccountId);
            origin = new(reference.Publisher, published, reference.App, reference.Version!, reference.Digest, caller.AccountId);
        }
        else
        {
            await catalog.Reload(reference.Publisher, reference.App).WaitAsync(ct);
            var listing = await marketplace.Read(reference.Publisher, reference.App, ct)
                ?? throw new KeyNotFoundException($"{reference.Publisher}/{reference.App} is not in the Marketplace.");
            if (listing.Source.Scope() == scope) { throw new InvalidOperationException($"{name} is already yours: you published it."); }
            if ((await plain.Get<IApp>(IApp.Key(scope, name)).Read().WaitAsync(ct)).Manifest.Title.Length > 0)
            { throw new InvalidOperationException($"You already have an app named {name}."); }
            bundle = await Bundle(listing.Source, listing.Publisher, reference.App, ct);
            origin = new(listing.Publisher, listing.Source, reference.App, bundle.Version);
            await CheckCapabilities(scope, bundle.Manifest, ct);
        }

        var requires = bundle.Manifest.Requires ?? new AppRequirements();
        if (request.Consent != AppConsent.Of(requires)) { return InstallOutcome.NeedsConsent(requires); }
        if (await Unbound(scope, bundle.Manifest, request.Bindings, ct) is { } refusal) { return InstallOutcome.Refused(refusal); }
        if ((requires.SemanticTypes is { Length: > 0 } || request.Bindings is { Count: > 0 }) && !await IsOwner(scope, caller))
        { return InstallOutcome.Refused(OwnerOnly); }

        // A retried exact install resumes a copy that exists already; only a copy this call created is rolled back.
        var existed = exact && (await plain.Get<IApp>(IApp.Key(scope, name)).Read().WaitAsync(ct)).Manifest.Title.Length > 0;
        var consented = origin with { Consented = Recorded(requires) };
        _ = exact
            ? await loader.Create(scope, name, bundle, consented, receipt!, ct)
            : await loader.Load(scope, name, bundle, consented, ct);
        var key = IApp.Key(scope, name);
        try
        {
            await Grant(key, requires.SemanticTypes, caller, ct);
            if (request.Bindings is { Count: > 0 } bindings)
            { await plain.Get<IApp>(key).Configure([], new(bindings, StringComparer.Ordinal)).WaitAsync(ct); }
        }
        catch
        {
            // No half-granted state: the copy and whatever was granted to it go, and the person sees the failure.
            if (!existed) { await RollBack(key, requires.SemanticTypes, caller); }
            throw;
        }
        return InstallOutcome.Installed(name);
    }

    // Every capability the app requires must be provided by a connected connection in this brain, before any state is
    // created. A requirement is a need, never a grant: this only reads connections and grants nothing.
    private async Task CheckCapabilities(string scope, AppManifest manifest, CancellationToken ct)
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
    private async Task<string?> Unbound(string scope, AppManifest manifest, Dictionary<string, string>? bindings, CancellationToken ct)
    {
        if (bindings is not { Count: > 0 }) { return null; }
        var connections = await plain.Get<IConnectionRequests>(IConnectionRequests.Key(scope)).ListConnections().WaitAsync(ct);
        foreach (var (slotName, id) in bindings)
        {
            if (manifest.Slots.FirstOrDefault(slot => slot.Name == slotName) is not { } slot)
            { return $"This app has no connection named {slotName}."; }
            if (!connections.Any(connection => connection.Id == id && connection.Status == "connected"
                && (slot.Capability is { } capability ? AppCapabilities.Satisfies(connection.Capabilities, capability) : connection.Kind == slot.Connector)))
            { return $"Choose a {slot.Binds} connection for {slot.Name}."; }
        }
        return null;
    }

    // A person, not an app or a script, who owns this brain: its Owner member, or the Open posture's owner in its own
    // brain, by the same rule the brain's authority grants with.
    private async Task<bool> IsOwner(string scope, CallerContext? caller)
        => identity is not null && caller is { Kind: CallerKind.User } && caller.StampedBy != TrustedEdge.AppProxy
            && BrainScope.Create(caller.AccountId, caller.BrainId).Id == scope
            && (AuthOptions.IsOpenPostureOwner(auth?.Value.Posture, caller)
                || (await identity.Membership(scope, caller.PrincipalId))?.Role == MemberRole.Owner);

    private async Task RollBack(string appKey, string[]? types, CallerContext? caller)
    {
        if (identity is not null && caller is not null)
        {
            foreach (var type in types ?? [])
            {
                try { await identity.RevokeAsOwner(caller, appKey, type, GrantMode.Always); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
        await plain.Get<IApp>(appKey).Delete();
    }

    // Installing is the one consent the install view asks for: the owner grants the app every semantic type it reads, always.
    private async Task Grant(string appKey, string[]? types, CallerContext? caller, CancellationToken ct)
    {
        if (types is not { Length: > 0 }) { return; }
        if (identity is null || caller is null) { throw new UnauthorizedAccessException(OwnerOnly); }
        foreach (var type in types)
        {
            await identity.GrantAsOwner(caller, new Grant
            {
                AppId = appKey,
                SemanticTypeId = type,
                Mode = GrantMode.Always,
                WorkspaceId = caller.BrainId,
            }).WaitAsync(ct);
        }
    }

    // An update applies as before while it asks for nothing new. One that adds a requirement (to what the person
    // consented to, or to the installed manifest for a copy recorded before consent was) changes nothing until the
    // consent is the digest of the new release's requirements; then only the added types are granted.
    public async Task<InstallOutcome> Update(UpdateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name;
        var scope = BrainScope.CurrentId();
        var target = plain.Get<IApp>(IApp.Key(scope, name));
        var current = await target.Read().WaitAsync(ct);
        if (current.MigrationTarget is { } alias)
        {
            var canonical = IApp.NameOf(scope, alias) ?? throw new InvalidOperationException("The app alias is outside this brain.");
            return await Update(request with { Name = canonical }, ct);
        }
        if (current.Origin is not { } origin)
        { throw new InvalidOperationException($"{name} was not installed from the Marketplace."); }
        var migrating = false;
        foreach (var migration in migrations) { migrating |= await migration.Required(scope, name, current, ct); }
        if (current.Edited && !migrating)
        { throw new InvalidOperationException($"{name} has your own edits; it no longer takes updates from {origin.Publisher}."); }
        if (origin.Source is AppSource.Shipped) { await catalog.Reload(origin.Publisher, origin.Name).WaitAsync(ct); }
        var listing = await marketplace.Read(origin.Publisher, origin.Name, ct) ?? throw Gone(origin.Source, origin.Publisher, origin.Name);
        if (!migrating && AppVersions.Compare(listing.Version, origin.Version) <= 0 && !Unconfirmed(current.Status)) { return InstallOutcome.Installed(name); }
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
            return InstallOutcome.Installed(name);
        }
        if (request.Consent != AppConsent.Of(requires)) { return InstallOutcome.NeedsConsent(added, requires); }
        var caller = CallerContextStamper.TryGet(out var stamped) ? stamped : null;
        if (added.SemanticTypes is { Length: > 0 } && !await IsOwner(scope, caller)) { return InstallOutcome.Refused(OwnerOnly); }
        await loader.Load(scope, name, bundle, next with { Consented = Recorded(requires) }, ct);
        await Grant(IApp.Key(scope, name), added.SemanticTypes, caller, ct);
        return InstallOutcome.Installed(name);
    }

    public async Task<MarketplaceEntry> Entry(MarketplaceListing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var scope = BrainScope.CurrentId();
        if (listing.Source.Scope() == scope) { return new(listing, true, false); }
        var local = await plain.Get<IApp>(IApp.Key(scope, listing.Name)).Read().WaitAsync(ct);
        var installed = local.Origin is { } origin && origin.Publisher == listing.Publisher && origin.Name == listing.Name;
        var update = installed && !local.Edited && AppVersions.Compare(listing.Version, local.Origin!.Version) > 0;
        return new(listing, installed, update);
    }

    // The published bundle a source holds: a person's brain or the features folder.
    public async Task<AppBundle> Bundle(AppSource source, string publisher, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            AppSource.Brain => await plain.Get<IApp>(IApp.Key(source.Scope()!, name)).ExportPublished().WaitAsync(ct),
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
