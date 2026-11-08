using DigitalBrain.Brains;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        await RequireBundleFor("Import", bundle);
        if (installation is not null)
        {
            await ValidateInstallation("Import", bundle, origin, installation);
            if (State.Manifest is not null && State.Installation == installation)
            {
                if (State.Edited || State.Status == AppStatus.Live) { return Describe(State); }
                var held = new AppBundle(Name(), State.Version, State.Manifest, State.Spec, new(State.Files, StringComparer.Ordinal));
                var changed = held.Version != installation.Publication.Version || AppPublicationDigest.Of(held) != installation.Publication.Digest;
                await Check("Import", new InstalledCopyIsUnchanged(changed));
                return await Land(State.Clone());
            }
        }
        await Check("Import", new AppNameIsFree(Name(), State.Manifest is not null));
        var next = State.Clone();
        next.BrainScopeId = BrainId();
        next.Principal = RuntimeCallerContext.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = bundle.Files;
        next.Settings = bundle.Manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(bundle.CompiledType))
        {
            next.PutSetting(InProcessAppRuntime.CompiledTypeKey, bundle.CompiledType);
        }
        next.Connections = new Dictionary<string, string>();
        next.Version = bundle.Version;
        next.Installation = installation;
        Adopt(next, origin);
        await Reopen();
        return await Land(next);
    }

    public async Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        await Check("Replace",
            new AppExists(Name(), State.Manifest is not null),
            new BundleNameIsValid(bundle.Name, Name()),
            new BundleVersionIsSemantic(bundle.Version));
        // A retried update at the version already held, with the same program, changes nothing.
        if (bundle.Version == State.Version && bundle.Spec == State.Spec
            && bundle.Files.Count == State.Files.Count
            && bundle.Files.All(file => State.Files.TryGetValue(file.Key, out var held) && held == file.Value)
            && State.Status == AppStatus.Live)
        {
            if (origin is null || (origin with { Version = bundle.Version } == State.Origin && !State.Edited)) { return Describe(State); }
            // The same program under a different shipped package is a different running program: it is assigned.
            var repackaged = origin.PackageDigest != State.Origin?.PackageDigest || (State.Edited && origin.PackageDigest is not null);
            var attached = State.Clone();
            Adopt(attached, origin);
            await Persist(attached);
            await RecordFeature();
            if (repackaged) { await Deploy(); }
            return Describe(State);
        }
        var next = State.Clone();
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = bundle.Files;
        foreach (var setting in bundle.Manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.PutSetting(setting.Name, setting.Default); } }
        if (!string.IsNullOrWhiteSpace(bundle.CompiledType))
        {
            next.PutSetting(InProcessAppRuntime.CompiledTypeKey, bundle.CompiledType);
        }
        next.Version = bundle.Version;
        Adopt(next, origin);
        // Settings, connections and the app's stored data stay; only the program is the bundle's.
        return await Land(next);
    }

    private static void Adopt(AppState next, AppOrigin? origin)
    {
        if (origin is null) { return; }
        next.Origin = origin with { Version = next.Version };
        next.Edited = false;
    }

    // A crossing bundle is held (program, version, origin) in one write before the runtime is asked;
    // only the runtime's answer makes it Live. Landing again is cheap: Assign is idempotent per program.
    private async Task<AppSnapshot> Land(AppState next)
    {
        next.Principal ??= RuntimeCallerContext.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
        next.Status = AppStatus.Deploying;
        AdvanceProgram(next);
        Record(next);
        await Persist(next);
        await EnsureAppPrincipal();
        try { await Deploy(); }
        catch (Exception failure)
        {
            var failed = State.Clone();
            failed.Status = AppStatus.Failed;
            failed.Activity = [.. failed.Activity.TakeLast(MaxActivity - 1), "deploy failed: " + failure.Message];
            await Persist(failed);
            throw;
        }
        var live = State.Clone();
        live.Status = AppStatus.Live;
        await Persist(live);
        return Describe(State);
    }

    private Task RequireBundleFor(string operation, AppBundle bundle)
        => Check(operation,
            new BundleNameIsValid(bundle.Name, Name()),
            new BundleVersionIsSemantic(bundle.Version));

    public async Task RecordOrigin(AppOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        // Recording the publisher's origin marks a copy unedited and shipped, which every ShippedApps.Is guard trusts:
        // an app behind the proxy (itself included) must never write its own.
        if (RuntimeCallerContext.TryGet(out var caller) && caller.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Only the kernel or a person records where an app came from."); }
        await RequireCreated("RecordOrigin");
        var next = State.Clone();
        next.Origin = origin;
        next.Edited = false;
        await Persist(next);
        await RecordFeature();
    }

    public async Task Restore()
    {
        if (State.MigrationPaused || State.Manifest is null) { return; }
        if (!HasProgram(State)) { return; }
        var configuration = Configuration(State);
        var inherited = RequestContext.Get(RuntimeCallerContext.RequestContextKey);
        var principal = State.Principal ?? await HostCaller.ForBrain(ServiceProvider, BrainId());
        if (principal is not null) { RuntimeCallerContext.Stamp(principal); }
        else { RequestContext.Remove(RuntimeCallerContext.RequestContextKey); }
        try
        {
            // Reassign the source on activation so a new host's contracts replace old calls, even when the app's version did not change.
            await Runtime.Deploy(Key, configuration, CancellationToken.None);
        }
        catch (Exception error)
        {
            ServiceProvider.GetRequiredService<ILogger<AppNeuron>>().LogError(error,
                "Could not restore the program of app {App} against the current contracts.", Key);
        }
        finally
        {
            if (inherited is null) { RequestContext.Remove(RuntimeCallerContext.RequestContextKey); }
            else { RequestContext.Set(RuntimeCallerContext.RequestContextKey, inherited); }
        }
    }

    // The in-process host is the only runtime; it holds nothing, so each call takes a fresh one.
    private InProcessAppRuntime Runtime => new(ServiceProvider);

    // An app runs only when it names a compiled module type; anything else is data.
    private static bool HasProgram(AppState state)
        => state.Settings.TryGetValue(InProcessAppRuntime.CompiledTypeKey, out var ct) && !string.IsNullOrWhiteSpace(ct);

    // The whole program, every time: the runtime decides what changed. No program means nothing runs.
    private async Task Deploy()
    {
        if (State.MigrationPaused) { return; }
        await RecordFeature();
        if (!HasProgram(State)) { await Runtime.Retire(Key, CancellationToken.None); return; }
        var inherited = RequestContext.Get(RuntimeCallerContext.RequestContextKey);
        var principal = State.Principal ?? await HostCaller.ForBrain(ServiceProvider, BrainId());
        if (principal is not null) { RuntimeCallerContext.Stamp(principal); }
        try
        {
            await Runtime.Deploy(Key, Configuration(State), CancellationToken.None);
        }
        finally
        {
            if (inherited is null) { RequestContext.Remove(RuntimeCallerContext.RequestContextKey); }
            else { RequestContext.Set(RuntimeCallerContext.RequestContextKey, inherited); }
        }
    }

    // Every program assignment lands in the kernel as this brain's feature, whatever its source:
    // the features folder, the Marketplace or the Creator. Owner is where it came from.
    private async Task RecordFeature()
    {
        if (ServiceProvider.GetService<IKernel>() is not { } kernel || State.Manifest is null || BrainId().Length == 0) { return; }
        var feature = new Feature(Name(), State.Version, State.Origin?.Publisher ?? BrainId());
        await kernel.LoadFeature(BrainId(), feature, []);
    }

    // The program reads brain.Setting("App") to find this neuron, brain.Setting(name) for each
    // setting and brain.Setting("Account__" + slot) for each connected account.
    private Dictionary<string, string> Configuration(AppState state)
    {
        var values = new Dictionary<string, string>(state.Settings, StringComparer.Ordinal);
        foreach (var (slot, id) in state.Connections) { values[AppRuntimeConfiguration.AccountBindingKey(slot)] = id; }
        values["App"] = Key;
        values["Brain"] = state.BrainScopeId ?? "";
        values["ProgramRevision"] = state.ProgramRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The host's public origin (empty when unset): Settings opens the account page there, Apps builds share links.
        var configuration = ServiceProvider.GetService<IConfiguration>();
        values["PublicOrigin"] = configuration?[DigitalBrain.Modules.Configuration.ModuleConfiguration.ConnectionsPublicOriginKey] ?? "";
        // Cloud sign-in contributes an account surface. The literal matches AuthIdentityProvider.AccountSurfaceSetting (view|surface).
        if (WritesAccountSurface(Name(), configuration?["IntoChatOS:Topology"]))
        {
            values["AccountSurface"] = "view|surface";
        }
        return values;
    }

    // The settings app on a cloud host mounts auth's account neuron. Other apps and a local host leave the setting unset.
    internal static bool WritesAccountSurface(string appName, string? topology) =>
        string.Equals(appName, "settings", StringComparison.OrdinalIgnoreCase)
        && string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    private Task ValidateInstallation(string operation, AppBundle bundle, AppOrigin? origin, AppInstallReceipt installation)
    {
        var reference = installation.Publication;
        if (!RuntimeCallerContext.TryGet(out var caller) || caller.AccountId != installation.AccountId
            || BrainScope.CurrentId() != BrainId())
        { throw new UnauthorizedAccessException("An installation belongs to its initiating account and destination brain."); }
        var matches = installation.OperationId != Guid.Empty && installation.TargetName == Name()
            && (origin is null ? null : origin with { Consented = null }) == new AppOrigin(reference.Publisher, reference.Source, reference.Name, reference.Version, reference.Digest, caller.AccountId)
            && bundle.Version == reference.Version && AppPublicationDigest.Of(bundle) == reference.Digest;
        return Check(operation, new ReceiptMatchesPublication(matches));
    }

    // Phase F: an installed app is a principal of its brain. Its membership (role App, under the brain owner's account)
    // exists from the moment the copy is created or lands until it is deleted. A host without the identity
    // authority, or a brain nobody owns yet, records none; the app proxy still stamps the app principal.
    private async Task EnsureAppPrincipal()
    {
        if (ServiceProvider.GetService<ICapabilityBroker>() is not { } broker || State.Manifest is not { } manifest) { return; }
        var brain = BrainId();
        IIdentity directory;
        Account? account;
        try { directory = await broker.Identity(); account = await directory.OwnerOfBrain(brain); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException) { return; }
        if (account is null) { return; }
        // Host install runs with the caller removed. Membership changes require the brain owner.
        // AppId stays empty: the directory accepts the owner, and a platform sentinel is not the owner.
        using var owner = RuntimeCallerContext.Stamp(CapabilityCallers.Of(account.OwnerPrincipalId, account.AccountId, brain));
        await directory.EnsureMember(new Member
        {
            PrincipalId = AppPrincipal.Id(Key),
            AccountId = account.AccountId,
            BrainId = brain,
            Role = MemberRole.App,
            DisplayName = manifest.Title,
            JoinedAt = clock.GetUtcNow(),
        });
    }

    private async Task RemoveAppPrincipal()
    {
        if (ServiceProvider.GetService<ICapabilityBroker>() is not { } broker) { return; }
        var brain = BrainId();
        var principal = AppPrincipal.Id(Key);
        IIdentity directory;
        Account? account;
        try { directory = await broker.Identity(); account = await directory.OwnerOfBrain(brain); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException) { return; }
        if (account is null) { return; }
        if (await directory.Membership(brain, principal) is not { Role: MemberRole.App }) { return; }
        using var owner = RuntimeCallerContext.Stamp(CapabilityCallers.Of(account.OwnerPrincipalId, account.AccountId, brain));
        await directory.RemovePrincipalMember(brain, principal);
    }


    public Task<RetainedRequest[]> RetainedRequests() => Task.FromResult(State.RequestRetention);
}
