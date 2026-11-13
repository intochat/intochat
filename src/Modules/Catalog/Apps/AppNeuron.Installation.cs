using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Modules.Configuration;
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
        RequireOriginWriter(origin);
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
        next.Principal = Context.Caller.ToCapabilityCaller() ?? CapabilityCallers.Of(BrainId(), BrainId(), BrainId());
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
        RequireOriginWriter(origin);
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

    // An origin marks the copy unedited and from its publisher, which every ShippedApps.Is guard trusts: an app behind the proxy
    // (itself included) never writes one; the kernel's host, the installer and a person do.
    private void RequireOriginWriter(AppOrigin? origin)
    {
        if (origin is not null && Context.Caller?.Actor is Actor.App)
        {
            throw new UnauthorizedAccessException("Only the kernel or a person records where an app came from.");
        }
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
        next.Principal ??= Context.Caller.ToCapabilityCaller() ?? CapabilityCallers.Of(BrainId(), BrainId(), BrainId());
        next.Status = AppStatus.Deploying;
        next.Refused = null;
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

    // Reassigns the program on activation, as its own one-way turn after the activation (see OnActivate): whatever goes wrong here is
    // logged and written to the activity, never thrown at the first caller.
    async Task IAppRestore.Restore()
    {
        try { await RestoreProgram(); }
        catch (Exception error)
        {
            ServiceProvider?.GetService<ILogger<AppNeuron>>()?.LogError(error,
                "Could not restore the program of app {App} against the current contracts.", Key);
            try
            {
                if (State.Manifest is not null)
                {
                    var next = State.Clone();
                    next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "restore failed: " + error.Message];
                    await Persist(next);
                }
            }
            catch (Exception recording)
            {
                ServiceProvider?.GetService<ILogger<AppNeuron>>()?.LogError(recording, "Could not record the restore failure of app {App}.", Key);
            }
        }
    }

    private async Task RestoreProgram()
    {
        if (State.Manifest is null || !HasProgram(State)) { return; }
        var configuration = Configuration(State);
        using (InProcessCallerScope.Enter())
        {
            // Reassign the source on activation so a new host's contracts replace old calls, even when the app's version did not change.
            await Runtime.Deploy(Key, configuration, CancellationToken.None);
        }
    }

    // The in-process host is the only runtime; it holds nothing, so each call takes a fresh one.
    private InProcessAppRuntime Runtime => new(services ?? throw new InvalidOperationException("No services"));

    // An app runs only when it names a compiled module type; anything else is data.
    private static bool HasProgram(AppState state)
        => state.Settings.TryGetValue(InProcessAppRuntime.CompiledTypeKey, out var ct) && !string.IsNullOrWhiteSpace(ct);

    // The whole program, every time: the runtime decides what changed. No program means nothing runs.
    private async Task Deploy()
    {
        await RecordFeature();
        if (!HasProgram(State)) { await Runtime.Retire(Key, CancellationToken.None); return; }
        using (InProcessCallerScope.Enter())
        {
            await Runtime.Deploy(Key, Configuration(State), CancellationToken.None);
        }
    }

    // Every program assignment lands in the kernel as this brain's feature, whatever its source:
    // the shipped catalog, the Marketplace or the Creator. Owner is where it came from.
    private async Task RecordFeature()
    {
        if (ServiceProvider?.GetService<IKernel>() is not { } kernel || State.Manifest is null || BrainId().Length == 0) { return; }
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
        var configuration = ServiceProvider?.GetService<IConfiguration>();
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
        string.Equals(appName, ShippedApps.Settings, StringComparison.OrdinalIgnoreCase)
        && string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    private Task ValidateInstallation(string operation, AppBundle bundle, AppOrigin? origin, AppInstallReceipt installation)
    {
        if (!RuntimeCallerContext.TryGet(out var caller) || caller.AccountId != installation.AccountId
            || BrainScope.CurrentId() != BrainId())
        { throw new UnauthorizedAccessException("An installation belongs to its initiating account and destination brain."); }
        var matches = PublishedRelease.IsReceiptMatching(installation, origin, bundle, Name(), caller.AccountId);
        return Check(operation, new ReceiptMatchesPublication(matches));
    }

    // Phase F: an installed app is a principal of its brain. Its membership (role App, under the brain owner's account)
    // exists from the moment the copy is created or lands until it is deleted. A host without the identity
    // authority, or a brain nobody owns yet, records none; the app proxy still stamps the app principal.
    private async Task EnsureAppPrincipal()
    {
        if (services?.GetService<ICapabilityBroker>() is not { } broker || State.Manifest is not { } manifest) { return; }
        var brain = BrainId();
        try
        {
            var directory = await broker.Identity();
            var account = await directory.OwnerOfBrain(brain);
            if (account is null) { return; }
            using (InProcessCallerScope.Enter())
            {
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
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
        {
            return;
        }
    }

    private async Task RemoveAppPrincipal()
    {
        if (services?.GetService<ICapabilityBroker>() is not { } broker) { return; }
        var brain = BrainId();
        var principal = AppPrincipal.Id(Key);
        try
        {
            var directory = await broker.Identity();
            var account = await directory.OwnerOfBrain(brain);
            if (account is null) { return; }
            if (await directory.Membership(brain, principal) is not { Role: MemberRole.App }) { return; }
            using (InProcessCallerScope.Enter())
            {
                await directory.RemovePrincipalMember(brain, principal);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
        {
            return;
        }
    }


    public Task<RetainedRequest[]> RetainedRequests() => Task.FromResult(State.RequestRetention);
}
