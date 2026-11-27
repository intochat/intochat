using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Log;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Manifest is null) { throw new InvalidDataException("App manifest is required."); }
        RequireOriginWriter(origin);
        await RequireBundleFor("Import", bundle);
        var files = bundle.Files ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var spec = bundle.Spec ?? string.Empty;
        if (installation is not null)
        {
            await ValidateInstallation("Import", bundle, origin, installation);
            if (State.Manifest is not null && State.Installation == installation)
            {
                if (State.Edited || State.Status == AppStatus.Live) { return Describe(State); }
                var held = new AppBundle(Name(), State.Version, State.Manifest, State.Spec, new(State.Files, StringComparer.Ordinal));
                await Check("Import", new InstalledCopyIsUnchanged(held, installation));
                return await Land(State.Clone());
            }
        }
        await Check("Import", new AppNameIsFree(Name(), State.Manifest));
        var manifest = bundle.Manifest.Validated();
        var next = State.Clone();
        next.BrainScopeId = BrainId();
        next.PublisherSource = CapturePublisher();
        next.Manifest = manifest;
        next.Spec = spec;
        next.Files = files;
        next.Settings = manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default ?? string.Empty, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(bundle.CompiledType))
        {
            next.PutSetting(AppTrust.CompiledTypeSetting, bundle.CompiledType);
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
        if (bundle.Manifest is null) { throw new InvalidDataException("App manifest is required."); }
        RequireOriginWriter(origin);
        await Check("Replace",
            new AppExists(Name(), State.Manifest),
            new BundleNameIsValid(bundle.Name, Name()),
            new BundleVersionIsSemantic(bundle.Version));
        var files = bundle.Files ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var spec = bundle.Spec ?? string.Empty;
        // A retried update at the version already held, with the same program, changes nothing.
        if (bundle.Version == State.Version && spec == State.Spec
            && files.Count == State.Files.Count
            && files.All(file => State.Files.TryGetValue(file.Key, out var held) && held == file.Value)
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
        var manifest = bundle.Manifest.Validated();
        var next = State.Clone();
        next.Manifest = manifest;
        next.Spec = spec;
        next.Files = files;
        foreach (var setting in manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.PutSetting(setting.Name, setting.Default ?? string.Empty); } }
        if (!string.IsNullOrWhiteSpace(bundle.CompiledType))
        {
            next.PutSetting(AppTrust.CompiledTypeSetting, bundle.CompiledType);
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
        if (origin is not null && CallerRules.IsApp(Context.Caller, out _))
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
        next.PublisherSource ??= CapturePublisher();
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
        if (State.Manifest is null) { return; }
        if (TryExtractSandboxPackage(State) is { } package)
        {
            await DeploySandboxPackageAsync(package, CancellationToken.None).ConfigureAwait(true);
            return;
        }
        VerifyCompiledProgramType(allowRetiredShipped: true);
    }

    // An app has a program only when it names a compiled module type (VerifyCompiledProgramType decides whether this host runs it).
    private static bool HasProgram(AppState state)
        => state.Settings.TryGetValue(AppTrust.CompiledTypeSetting, out var ct) && !string.IsNullOrWhiteSpace(ct);

    // The whole program, every time: the runtime decides what changed. No program means nothing runs.
    private async Task Deploy()
    {
        await RecordFeature();
        if (TryExtractSandboxPackage(State) is { } package)
        {
            await DeploySandboxPackageAsync(package, CancellationToken.None).ConfigureAwait(true);
            return;
        }
        VerifyCompiledProgramType(allowRetiredShipped: false);
    }

    // A named compiled program must be one this host can run. The unedited shipped copy of an app the host itself loaded from a
    // registered compiled-app assembly passes on that provenance, so its stored type name may be stale after the type moved; it is
    // not parsed and never rewritten. During startup restore, an unedited os-shipped non-root app that the host no longer ships
    // (a retired shipped app such as uikit) is cleanly skipped instead of logging an error. Every other record passes only when
    // its stored type name resolves.
    private void VerifyCompiledProgramType(bool allowRetiredShipped = false)
    {
        if (!State.Settings.TryGetValue(AppTrust.CompiledTypeSetting, out var typeName)
            || string.IsNullOrWhiteSpace(typeName))
        {
            return;
        }

        var hosted = hostedAppTypes ?? services?.GetService<IHostedAppTypes>();
        var isUneditedShipped = ShippedApps.Is(Describe(State), Name());
        if (hosted is not null && isUneditedShipped && hosted.IsHostLoaded(Name()))
        {
            return;
        }

        var resolved = Type.GetType(typeName, throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                .FirstOrDefault(type => type is not null);
        if (resolved is not null)
        {
            return;
        }

        if (allowRetiredShipped
            && isUneditedShipped
            && hosted is not null
            && !hosted.IsHostLoaded(Name())
            && !ShippedApps.Roots.Contains(Name()))
        {
            (logger ?? ServiceProvider?.GetService<ILogger<AppNeuron>>())?.LogInformation(
                "Skipping restore of retired shipped app {App}; compiled type '{Type}' is no longer hosted.",
                Key,
                typeName);
            return;
        }

        throw new InvalidOperationException($"Compiled app type '{typeName}' could not be resolved.");
    }

    private AppPackage? TryExtractSandboxPackage(AppState state)
        => AppPackageExtensions.TryFromFiles(
            Name(),
            state.Version,
            state.Files,
            state.Manifest?.Title,
            state.Manifest?.Description,
            state.Publisher ?? state.Origin?.Publisher);

    private async Task DeploySandboxPackageAsync(AppPackage package, CancellationToken cancellationToken)
    {
        var compiler = ServiceProvider?.GetService<AppPackageCompiler>();
        var bridge = ServiceProvider?.GetService<ISandboxRuntimeBridge>();
        if (compiler is null || bridge is null) { return; }

        var compiled = compiler.Compile(package);
        if (!compiled.Succeeded)
        {
            throw new InvalidOperationException($"Failed to compile app '{Name()}': {string.Join("; ", compiled.Errors)}");
        }

        var compiledPackage = compiled.EnrichedPackage ?? package;
        var settingsJson = System.Text.Json.JsonSerializer.Serialize(State.Settings);
        var deployment = await bridge.DeployPackageAsync(
            BrainId(),
            compiledPackage,
            compiled.AssemblyBytes,
            stateJson: null,
            settingsJson: settingsJson,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        if (!deployment.Succeeded)
        {
            throw new InvalidOperationException(deployment.Error ?? $"Sandbox failed to deploy '{Name()}'.");
        }

        await Deployed(deployment.DiscoveredCommands, deployment.DiscoveredSchedules);

        if (compiledPackage.Needs.Signals.Length > 0 && ServiceProvider is not null)
        {
            try
            {
                using (Edge?.Enter(Edge.Module("apps.catalog", Id.Brain)))
                {
                    await SiloNeurons.Get<ISandboxSignalRouter>(ServiceProvider, ISandboxSignalRouter.Key(BrainId()))
                        .BindSignalsAsync(compiledPackage.Needs.Signals, cancellationToken)
                        .ConfigureAwait(true);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                ServiceProvider.GetService<ILogger<AppNeuron>>()?.LogWarning(
                    error,
                    "Could not bind sandbox signal router for app '{App}' on brain '{Brain}'.",
                    Name(),
                    BrainId());
            }
        }
    }

    // The deployed program as a synapse: its commands and its [Every] schedules (each due tick is a run of the app). Null clears it.
    private async Task Deployed(string[]? commands, string[]? schedules)
    {
        if (ServiceProvider is null) { return; }
        try
        {
            using (Edge?.Enter(Edge.Module("apps.catalog", Id.Brain)))
            {
                var deployed = SiloNeurons.Get<IntoChat.OS.Modules.Apps.AppRuns.IDeployedApp>(ServiceProvider, IntoChat.OS.Modules.Apps.AppRuns.IDeployedApp.KeyOf(BrainId(), Name()));
                await (commands is null || schedules is null ? deployed.Clear() : deployed.Set(Name(), commands, schedules)).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ServiceProvider.GetService<ILogger<AppNeuron>>()?.LogWarning(error, "Could not record the deployed app '{App}' on brain '{Brain}'.", Name(), BrainId());
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

    private Task ValidateInstallation(string operation, AppBundle bundle, AppOrigin? origin, AppInstallReceipt installation)
    {
        if (Context.Caller is not { For.Account: var account } caller || account != installation.AccountId
            || caller.Brain.Value != BrainId())
        { throw new UnauthorizedAccessException("An installation belongs to its initiating account and destination brain."); }
        return Check(operation, new ReceiptMatchesPublication(installation, origin, bundle, Name(), account));
    }

    // Phase F: an installed app is a principal of its brain. Its membership (role App, under the brain owner's account)
    // exists from the moment the copy is created or lands until it is deleted. A host without the identity
    // authority, a brain nobody owns yet, or a non-owner caller (e.g. Kernel during ephemeral birth, App, Agent,
    // Module, Scheduler, or non-owner Person) records none; the app proxy still stamps the app principal.
    private async Task EnsureAppPrincipal()
    {
        if (services?.GetService<ICapabilityBroker>() is not { } broker || State.Manifest is not { } manifest) { return; }
        if (Context.Caller?.Actor is not Actor.Person person) { return; }
        var brain = BrainId();
        try
        {
            var directory = await broker.Identity();
            var account = await directory.OwnerOfBrain(brain);
            if (account is null) { return; }
            if (await directory.Membership(brain, person.Principal) is not { Role: MemberRole.Owner }) { return; }
            var principalId = AppPrincipal.Id(Key);
            if (await directory.Membership(brain, principalId) is { Role: MemberRole.App }) { return; }
            using (Edge?.EnterInProcess())
            {
                await directory.EnsureMember(new Member
                {
                    PrincipalId = principalId,
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
            var logger = services?.GetService<ILoggerFactory>()?.CreateLogger<AppNeuron>() ?? OsConsoleLog.For<AppNeuron>();
            logger.LogSwallowed(error, "ensuring optional app principal");
            return;
        }
    }

    private async Task RemoveAppPrincipal()
    {
        if (services?.GetService<ICapabilityBroker>() is not { } broker) { return; }
        if (Context.Caller?.Actor is not Actor.Person person) { return; }
        var brain = BrainId();
        var principal = AppPrincipal.Id(Key);
        try
        {
            var directory = await broker.Identity();
            var account = await directory.OwnerOfBrain(brain);
            if (account is null) { return; }
            if (await directory.Membership(brain, person.Principal) is not { Role: MemberRole.Owner }) { return; }
            if (await directory.Membership(brain, principal) is not { Role: MemberRole.App }) { return; }
            using (Edge?.EnterInProcess())
            {
                await directory.RemovePrincipalMember(brain, principal);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException)
        {
            var logger = services?.GetService<ILoggerFactory>()?.CreateLogger<AppNeuron>() ?? OsConsoleLog.For<AppNeuron>();
            logger.LogSwallowed(error, "removing optional app principal");
            return;
        }
    }
}
