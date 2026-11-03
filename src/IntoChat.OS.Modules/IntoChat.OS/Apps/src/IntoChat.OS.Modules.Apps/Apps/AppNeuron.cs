using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flame;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Modules.Apps;

// The app: the only durable thing in the module. Manifest, spec, files, connections,
// semver history, status and activity all live here; the Builder is a cache that writes
// its results back in.
// A plain neuron (06-08) with the persisted AppState ("app2", clean break R4-24). Delete clears the record; until the next save
// the app then reads as never created (the DigitalBrain base keeps the last state in memory, so the cleared view is held here).
[DigitalBrain.Neuron(IntoChat.OS.Connections.AppGrainType.Name, State = "app2")]
internal sealed partial class AppNeuron(
    TimeProvider clock, IOptions<AppsOptions> options,
    IEnumerable<IAppRuntime> runtimes, IEnumerable<IAppStorageHook> storageHooks)
    : DigitalBrain.Neuron<AppState>, IApp, IAppDeploy, IAppMigration
{
    private static readonly AppState Cleared = new();
    // Whether the app has a record: created apps always carry a manifest.
    private bool Exists => State.Manifest is not null;
    private IServiceProvider ServiceProvider => Context.Services;
    // The os full key "{brain}/apps/{name}" the app was addressed by as a grain.
    private string Key => $"{Id.Brain.Value}/{Id.Key}";

    // Every activation reconciles an interrupted turn, then queues the program's restore as its own turn.
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Interrupt();
        if (!State.MigrationPaused && State.Manifest is not null && HasProgram(State))
        { Defer(() => Get<IAppDeploy>(Id).Restore()); }
    }

    private Task Save(AppState next, DigitalBrain.Signal first, DigitalBrain.Signal second, DigitalBrain.Signal third) => base.Save(next, [first, second, third]);

    // Deleting the record ends the app's existence (the kernel retires the instance), then says so.
    private async Task Clear(DigitalBrain.Signal changed)
    {
        await Context.State.Clear();
        _ = Cleared;
        await Publish(changed);
    }

    // Another app of this brain, by its os key.
    private DigitalBrain.NeuronId AppId(string key) => new(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, key[(Id.Brain.Value.Length + 1)..]);

    // The platform-wide Marketplace is still a grain outside every brain.
    private IMarketplace Marketplace => SiloNeurons.Get<IMarketplace>(Context.Services, IMarketplace.Key);
    private const int MaxHistory = 3;
    private const int MaxActivity = 50;
    private const int MaxPayloadLength = 64 * 1024;
    private const int MaxReceipts = 64;
    private const int RetainedCapacity = 64;
    private const int MaxSynapses = 64;
    private const int MaxSentenceLength = 4000;
    internal const int MaxFocus = 16;
    internal const int MaxFocusKeyLength = 512;

    public Task<AppSnapshot> Read() => Task.FromResult(Describe(State));

    public async Task<AppSnapshot> Create(AppManifest manifest, string spec)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await Check("Create", new AppNameIsFree(Name(), State.Manifest is not null));
        var next = State.Clone();
        next.Manifest = manifest.Validated();
        next.BrainScopeId = BrainId();
        next.Principal = CallerContextStamper.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
        next.Spec = spec ?? "";
        next.Settings = manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        next.Version = "0.1.0";
        next.Status = AppStatus.Drafted;
        await Reopen();
        Record(next);
        await Persist(next);
        await EnsureAppPrincipal();
        return Describe(State);
    }

    public async Task<AppSnapshot> SaveSpec(string spec, Guid operationId)
    {
        await Check("SaveSpec",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        if (Replayed(operationId)) { return Describe(State); }
        var next = State.Clone();
        next.Edited = true;
        next.Spec = spec;
        next.Version = Bump(next.Version, minor: true);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId)
    {
        await Check("SaveManifest",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        ArgumentNullException.ThrowIfNull(manifest);
        if (Replayed(operationId)) { return Describe(State); }
        var next = State.Clone();
        next.Edited = true;
        next.Manifest = manifest.Validated();
        // Newly declared settings get their defaults; chosen values survive.
        foreach (var setting in manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.PutSetting(setting.Name, setting.Default); } }
        next.Version = Bump(next.Version, minor: false);
        await RequireCompiles(next);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId)
    {
        await Check("WriteBehavior",
            new AppExists(Name(), State.Manifest is not null),
            new ProgramIsNotPaused(State.MigrationPaused));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        await Check("WriteBehavior", new ProgramPathIsWritable(path ?? ""));
        if (Replayed(operationId)) { return Describe(State); }
        var next = State.Clone();
        next.Edited = true;
        next.PutFile(path!, source);
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        AdvanceProgram(next);
        // The whole program goes to the runtime before anything is persisted: a write the
        // compiler refuses leaves the app's files exactly as they were.
        IReadOnlyDictionary<string, string>? hashes = null;
        if (Runtime is { } runtime)
        {
            var sources = Sources(next);
            hashes = await runtime.Deploy(Key, sources, Configuration(next), CancellationToken.None);
        }
        // A fast-path write names its deployed synapse on the request; CompleteSynapse lands the outcome.
        var requested = Array.FindIndex(next.Synapses, synapse => synapse.OperationId == operationId && synapse.Status == SynapseStatus.Requested);
        if (requested >= 0) { next.Synapses[requested] = next.Synapses[requested] with { Path = path!, Hash = hashes?.GetValueOrDefault(path!) }; }
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> WriteFile(string path, string source, Guid operationId)
    {
        await Check("WriteFile",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        await Check("WriteFile", new FilePathIsNotAProgram(path));
        if (Replayed(operationId)) { return Describe(State); }
        var next = State.Clone();
        next.Edited = true;
        next.PutFile(path, source);
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections)
    {
        await Check("Configure",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        // A storage hook vetoes a binding change its data depends on, before anything changes.
        foreach (var (slot, id) in connections ?? [])
        {
            if (State.Connections.TryGetValue(slot, out var current) && current == id) { continue; }
            foreach (var hook in storageHooks)
            { await hook.RequireRebindable(State.BrainScopeId ?? "", Key, slot, CancellationToken.None); }
        }
        var next = State.Clone();
        foreach (var (name, value) in settings ?? []) { next.PutSetting(name, value); }
        foreach (var (slot, id) in connections ?? []) { next.PutConnection(slot, id); }
        AdvanceProgram(next);
        await Persist(next);
        // The running program reads its settings from the deployment configuration, so a
        // configure hands it the whole thing again.
        await Deploy();
        return Describe(State);
    }

    public async Task<AppSnapshot> Rollback(string version)
    {
        string[] known = [.. State.History.Select(item => item.Version)];
        await Check("Rollback",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null),
            new VersionExists(version, known));
        var record = State.History.First(item => item.Version == version);
        var next = State.Clone();
        next.Edited = true;
        next.Manifest = record.Manifest;
        next.Spec = record.Spec;
        next.Files = record.Files;
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        AdvanceProgram(next);
        await Persist(next);
        // Programs the target version lacks stop with the rest: one program, assigned whole.
        await Deploy();
        return Describe(State);
    }

    public async Task<AppSnapshot> BeginBuild(string? instruction)
    {
        var building = State.Status == AppStatus.Building
            && clock.GetUtcNow() - State.BuildStartedAt < TimeSpan.FromSeconds(options.Value.BuildFreshForSeconds);
        await Check("BeginBuild",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null),
            new BuilderIsIdle(building));
        var next = State.Clone();
        next.Status = AppStatus.Building;
        next.BuildAttempt = Guid.NewGuid();
        next.BuildStartedAt = clock.GetUtcNow();
        next.Activity = string.IsNullOrWhiteSpace(instruction) ? ["build started"] : ["build started: " + instruction.Trim()];
        await Persist(next);
        return Describe(State);
    }

    private static readonly string[] LiveStates = ["working", "reading", "searching", "accepted"];

    // A turn-in-progress activity (not listening or waiting, which an idle app on a live synapse keeps) retained across a deactivation belongs to a turn that died with the process; a late
    // listener must not see it as current. Do not republish the activity; reconcile its reserved
    // presentation flags in the same durable write so the visible presence is not left running.
    // 06-08: a late listener is replayed the app's newest journalled AppActivity (the runtime's retention), so the reconciled
    // activity is journalled with the reconciled flags; live listeners hear the interruption too.
    internal Task Interrupt()
    {
        if (!Exists || State.LastActivity is not { State: var state } retained
            || !LiveStates.Contains(state, StringComparer.Ordinal))
        { return Task.CompletedTask; }
        var next = State.Clone();
        next.ActivitySequence++;
        var interrupted = retained with { State = "attention", Detail = "interrupted" };
        next.LastActivity = interrupted;
        var presentation = ApplyActivityPresentation(next, "attention");
        return presentation is null ? Save(next, interrupted) : Save(next, interrupted, presentation);
    }

    public Task<Guid> CurrentBuildAttempt() => Task.FromResult(State.BuildAttempt);

    public Task Progress(string state, string detail) => Progress(state, detail, []);

    public async Task Progress(string state, string detail, Dictionary<string, string> blackboard)
    {
        // A background writer may report after deletion; activity must not recreate its app record.
        if (State.Manifest is null) { return; }
        var declared = State.Presentation?.Root?.Sphere?.BlackboardKeys;
        var keyRules = declared is not null && blackboard is { Count: > 0 }
            ? blackboard.Keys.Select(key => (Rule)new BlackboardKeyIsDeclared(key, declared)).ToArray()
            : [];
        await Check("Progress", [new ActivityStateIsKnown(state ?? ""), .. keyRules]);
        detail = (detail ?? "").Trim();
        // In-memory update without State.Clone() or Save() disk flushes
        var building = State.Status == AppStatus.Building && detail.Length > 0
            && State.Activity.LastOrDefault() != detail;
        if (building) { State.Activity = [.. State.Activity.TakeLast(MaxActivity - 1), detail]; }
        var activity = new AppActivity(Name(), state!, detail);
        State.ActivitySequence++;
        State.LastActivity = activity;
        var presentation = ApplyActivityPresentation(State, state!, blackboard);
        await PublishEphemeral(activity);
        if (presentation is not null)
        { await PublishEphemeral(presentation); }
    }

    private PresentationChanged? ApplyActivityPresentation(AppState next, string state, Dictionary<string, string>? extra = null)
    {
        var running = state is "working" or "reading" or "searching" or "listening" ? "true" : "false";
        var attention = state == "attention" ? "true" : "false";
        var current = next.Presentation ?? new(next.ProgramRevision, 0, null, null, new(StringComparer.Ordinal));
        var declared = current.Root?.Sphere?.BlackboardKeys;
        if (extra is { Count: > 0 } && declared is null)
        {
            // The sphere is published by another behavior. Until it exists, the activity still commits
            // and keys that have nowhere to go wait for a later write.
            extra = null;
        }
        var board = new Dictionary<string, string>(current.Blackboard, StringComparer.Ordinal)
        { ["running"] = running, ["attention"] = attention };
        if (extra is not null)
        { foreach (var (key, value) in extra) { board[key] = value; } }
        if (board.Count == current.Blackboard.Count && board.All(pair => current.Blackboard.TryGetValue(pair.Key, out var held) && held == pair.Value))
        { return null; }
        next.Presentation = current with { Revision = checked(current.Revision + 1), Blackboard = board };
        return new(Key, next.ProgramRevision, next.Presentation.Revision);
    }

    public async Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null)
    {
        if (State.MigrationPaused) { return Describe(State); }
        if (State.Status != AppStatus.Building || attempt != State.BuildAttempt) { return Describe(State); }
        var next = State.Clone();
        if (error is not null || files is not { Count: > 0 })
        {
            next.Status = AppStatus.Failed;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "build failed: " + (error ?? "the Builder produced no files")];
            await Persist(next);
            return Describe(State);
        }
        if (files.ContainsKey(AppFiles.DeclarationPath))
        {
            next.Status = AppStatus.Failed;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), $"build failed: {AppFiles.DeclarationPath} belongs to the Author, not the Builder"];
            await Persist(next);
            return Describe(State);
        }
        var firstBuild = State.Files.Count == 0;
        if (manifest is not null)
        {
            next.Manifest = manifest.Validated();
            foreach (var setting in manifest.Settings)
            { if (!next.Settings.ContainsKey(setting.Name)) { next.PutSetting(setting.Name, setting.Default); } }
        }
        foreach (var (path, source) in files) { next.PutFile(path, source); }
        next.Edited = true;
        next.Version = Bump(next.Version, minor: firstBuild);
        next.Status = AppStatus.Live;
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "build finished"];
        Record(next);
        AdvanceProgram(next);
        await Persist(next);
        // The Builder's program lands whole; one that does not compile is a failed build.
        try { await Deploy(); }
        catch (InvalidOperationException failure)
        {
            var failed = State.Clone();
            failed.Status = AppStatus.Failed;
            failed.Activity = [.. failed.Activity.TakeLast(MaxActivity - 1), "build failed: " + failure.Message];
            await Persist(failed);
        }
        return Describe(State);
    }

    public async Task<AppBundle> Export()
    {
        await RequireCreated("Export");
        await RequireCompiles();
        var manifest = State.Manifest!;
        return new AppBundle(Name(), State.Version, manifest, State.Spec,
            new(State.Files, StringComparer.Ordinal));
    }

    public async Task<AppSnapshot> Publish()
    {
        await Check("Publish",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        await RequireCompiles();
        CallerContextStamper.TryGet(out var caller);
        await Check("Publish", new PublisherIsSignedIn(caller?.PersonId));
        if (BrainScope.Create(caller!.AccountId, caller.BrainId).Id != BrainId())
        { throw new UnauthorizedAccessException("Only the brain that owns an app can publish it."); }
        // A listing is one app: the same name from another of the publisher's brains is refused,
        // never a silent takeover of what installers already trust.
        var listed = await Marketplace.Read(caller.PersonId, Name());
        var next = State.Clone();
        var manifest = next.Manifest!;
        next.PublishedVersion = next.Version;
        next.Published = new AppVersionRecord(next.Version, clock.GetUtcNow(), manifest, next.Spec, next.Files);
        next.Publisher = caller.PersonId;
        var releases = Releases();
        var release = Release(next.Published, caller.PersonId, new AppSource.Brain(caller.AccountId, caller.BrainId));
        var existing = releases.FirstOrDefault(item => item.Bundle.Version == next.Version);
        await Check("Publish",
            new PublicationIsNotForeign(listed?.Source.Scope(), BrainId()),
            new VersionContentIsUnchanged(existing is null || existing.Publication.Reference == release.Publication.Reference));
        next.Releases = existing is null ? [.. releases, release] : releases;
        if (existing is not null)
        { next.Published = next.Published with { SavedAt = existing.Publication.PublishedAt }; }
        var name = Name();
        await Save(next, new AppChanged(name, next.Version, next.Status), new AppPublished(name, next.Version));
        // State first: a failed listing write is repaired by publishing again, never the reverse
        // (a listing whose bundle Install cannot read).
        await Marketplace.Upsert(new MarketplaceListing(
            caller.PersonId, new AppSource.Brain(caller.AccountId, caller.BrainId), name, manifest.Title, manifest.Description,
            next.Version, manifest.Icon, manifest.Requires));
        return Describe(State);
    }

    public Task<AppBundle?> ExportVersion(string version)
    {
        var record = State.History.LastOrDefault(item => item.Version == version);
        return Task.FromResult(record is null ? null
            : new AppBundle(Name(), record.Version, record.Manifest, record.Spec, new(record.Files, StringComparer.Ordinal)));
    }

    public async Task<AppBundle> ExportPublished()
    {
        await Check("ExportPublished", new HasPublishedVersion(State.Published is not null));
        var published = State.Published!;
        return new AppBundle(Name(), published.Version, published.Manifest, published.Spec,
            new(published.Files, StringComparer.Ordinal));
    }

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
        next.Principal = CallerContextStamper.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = bundle.Files;
        next.Settings = bundle.Manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(bundle.CompiledType))
        {
            next.PutSetting(InProcessAppRuntime.CompiledTypeKey, bundle.CompiledType);
        }
        if (bundle.PrebuiltBehaviors is { Count: > 0 })
        {
            next.PutSetting(InProcessAppRuntime.PrebuiltBehaviorsJsonKey, InProcessAppRuntime.SerializePrebuiltBehaviors(bundle.PrebuiltBehaviors));
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
        if (bundle.PrebuiltBehaviors is { Count: > 0 })
        {
            next.PutSetting(InProcessAppRuntime.PrebuiltBehaviorsJsonKey, InProcessAppRuntime.SerializePrebuiltBehaviors(bundle.PrebuiltBehaviors));
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
        next.Principal ??= CallerContextStamper.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
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
        if (CallerContextStamper.TryGet(out var caller) && caller.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Only the kernel or a person records where an app came from."); }
        await RequireCreated("RecordOrigin");
        var next = State.Clone();
        next.Origin = origin;
        next.Edited = false;
        await Persist(next);
        await RecordFeature();
    }


    public async Task<AppSnapshot> SaveToSource()
    {
        var development = ServiceProvider.GetService<IHostEnvironment>() is { } environment && environment.IsDevelopment();
        var fromFeature = State.Origin is { Source: AppSource.Shipped };
        await Check("SaveToSource",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null),
            new SaveToSourceIsAvailable(development, fromFeature, Name()));
        var origin = State.Origin!;
        var catalog = ServiceProvider.GetRequiredService<FeatureCatalog>();
        var exported = await Export();
        var shipped = await catalog.Find(origin.Publisher, origin.Name);
        var version = Bump(Newest(exported.Version, shipped?.Version ?? exported.Version), minor: false);
        var saved = await catalog.SaveToSource(origin.Publisher, exported with { Name = origin.Name, Version = version });
        var next = State.Clone();
        next.Version = saved.Version;
        next.Origin = origin with { Version = saved.Version };
        next.Edited = false;
        Record(next);
        await Persist(next);
        return Describe(State);
    }

    private static string Newest(string left, string right)
        => Version.Parse(left) >= Version.Parse(right) ? left : right;

    public async Task Delete()
    {
        await Check("Delete", new ProgramIsNotPaused(State.MigrationPaused));
        if (State.Manifest is null) { return; }
        var key = Key;
        // Only this app's own listing comes down with it.
        if (State.Publisher is { } publisher
            && await Marketplace.Read(publisher, Name()) is { } listed
            && listed.Source.Scope() == BrainId())
        { await Marketplace.Remove(publisher, Name()); }
        // Everything the app owns goes with it: its program, then its storage.
        if (Runtime is { } runtime) { await runtime.Retire(key, CancellationToken.None); }
        if (ServiceProvider.GetService<IKernel>() is { } kernel) { await kernel.UnloadFeature(BrainId(), Name()); }
        foreach (var hook in storageHooks) { await hook.OnRetired(State.BrainScopeId ?? "", key, CancellationToken.None); }
        await RemoveAppPrincipal();
        // Clearing the record ends the app's existence: the kernel retires the instance.
        await Clear(new AppChanged(Name(), Cleared.Version, Cleared.Status));
    }

    private async Task Reopen()
    {
        foreach (var hook in storageHooks) { await hook.OnCreated(BrainId(), Key, CancellationToken.None); }
    }

    public async Task<Guid> Request(string operation, string input)
    {
        await RequireCreated("Request");
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(input);
        string[] operations = [.. State.Manifest!.Operations.Select(candidate => candidate.Name)];
        await Check("Request",
            new OperationIsDeclared(operation, operations, Name()),
            new RequestPayloadFits(input.Length));
        var id = Guid.NewGuid();
        if (State.MigrationTarget is { } target)
        {
            await Get<IAppMigration>(AppId(target)).Accept(operation, input, id);
            return id;
        }
        var requested = new AppRequested(operation, input, id);
        if (await Rejection(requested) is { } rejected)
        {
            await Publish(new AppRejected(operation, rejected));
            await Progress("attention", $"{rejected.Rule} refused {operation}: {rejected.Reason}");
            throw new RuleViolationException(new Rejected(operation, [rejected.Rule], [rejected.Reason]));
        }
        // A request is a fact for the app's synapses; it is published for whoever is watching and kept
        // for whoever subscribes later, so a synapse that starts after the request still gets it.
        var next = State.Clone();
        var retained = new RetainedRequest(next.RequestSequence + 1, requested);
        next.RequestSequence = retained.Sequence;
        // During cutover two independent retained windows are combined. Do not immediately
        // truncate half of that adopted window when the first new command arrives.
        var capacity = next.Migration is null ? RetainedCapacity : 2 * RetainedCapacity;
        next.RequestRetention = [.. next.RequestRetention.TakeLast(capacity - 1), retained];
        await Save(next, new AppRequested(operation, input, id));
        return id;
    }

    public async Task<AppSnapshot> WriteSynapse(string sentence, string[] focus, Guid operationId)
    {
        await Check("WriteSynapse",
            new ProgramIsNotPaused(State.MigrationPaused),
            new AppExists(Name(), State.Manifest is not null));
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        string[] keys = [.. (focus ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal)];
        await Check("WriteSynapse",
            new SynapseSentenceFits(sentence.Length),
            new FocusFits(keys));
        if (Replayed(operationId) || State.Synapses.Any(synapse => synapse.OperationId == operationId)) { return Describe(State); }
        await Check("WriteSynapse", new SandboxIsAvailable(ServiceProvider.GetService<IScriptSandbox>()?.CanRun == true));
        var next = State.Clone();
        next.Synapses = [.. next.Synapses.TakeLast(MaxSynapses - 1),
            new AppSynapse(operationId, sentence.Trim(), keys, SynapseStatus.Requested, clock.GetUtcNow(), [])];
        await Persist(next);
        // The writer runs on the work queue, outside this turn, and calls back as an outside caller.
        ServiceProvider.GetRequiredService<ReactionWriter>().Enqueue(Key, operationId, sentence.Trim(), keys);
        return Describe(State);
    }

    public async Task<StagedProgram> Stage(Guid operationId, string path, string source)
    {
        await Check("Stage",
            new AppExists(Name(), State.Manifest is not null),
            new ProgramIsNotPaused(State.MigrationPaused));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var index = Array.FindIndex(State.Synapses, synapse => synapse.OperationId == operationId);
        await Check("Stage",
            new ProgramPathIsWritable(path ?? ""),
            new SynapseRequestIsPending(index >= 0 && State.Synapses[index].Status == SynapseStatus.Requested));
        var sources = Sources(State);
        sources[path!] = source;
        var entry = State.Synapses[index];
        if (entry.Path != path || entry.Source != source)
        {
            var next = State.Clone();
            next.Synapses[index] = entry with { Path = path, Source = source };
            AdvanceProgram(next);
            await Persist(next);
        }
        return new(sources, Configuration(State), State.Principal);
    }

    // A deleted app has no files, so a writer that outlived it deploys an empty program, which retires the runtime.
    public async Task<StagedProgram> Program()
    {
        if (!Exists || State.MigrationPaused) { return new([], Configuration(State), State.Principal); }
        var next = State.Clone();
        AdvanceProgram(next);
        await Persist(next);
        return new(Sources(State), Configuration(State), State.Principal);
    }

    // A deleted app holds no requests, so a late Unsettle answers its empty snapshot and writes nothing.
    public async Task<AppSnapshot> Unsettle(Guid operationId, string reason, long elapsedMs, int attempts, string? baseSynapse)
    {
        var index = Array.FindIndex(State.Synapses, synapse => synapse.OperationId == operationId);
        if (index < 0 || State.Synapses[index].Status != SynapseStatus.Requested) { return Describe(State); }
        var entry = State.Synapses[index];
        var next = State.Clone();
        next.Synapses[index] = entry with { Status = SynapseStatus.Unsettled, Errors = [reason], ElapsedMs = elapsedMs, Attempts = attempts, Base = baseSynapse };
        await Save(next, new AppChanged(Name(), next.Version, next.Status), new SynapseRejected(entry.Sentence, [reason]));
        return Describe(State);
    }

    public async Task<AppSnapshot> CompleteSynapse(Guid operationId, string[] errors, long elapsedMs, int attempts, string? baseSynapse = null, string? hash = null)
    {
        if (State.MigrationPaused) { return Describe(State); }
        var index = Array.FindIndex(State.Synapses, synapse => synapse.OperationId == operationId);
        if (index < 0 || State.Synapses[index].Status != SynapseStatus.Requested) { return Describe(State); }
        var entry = State.Synapses[index];
        string[] refused = [.. errors ?? []];
        // A synapse WriteBehavior deployed (nothing staged) is Written, even when its caller gave up waiting on it.
        if (entry.Source is null && entry.Path is { } deployed && State.Files.ContainsKey(deployed)) { refused = []; }
        if (refused.Length == 0 && entry.Path is null) { refused = ["The writer reported a synapse it never wrote."]; }
        var next = State.Clone();
        // A staged synapse the runtime now runs joins the app's files.
        if (refused.Length == 0 && entry.Source is { } staged)
        {
            next.Edited = true;
            next.PutFile(entry.Path!, staged);
            next.Version = Bump(next.Version, minor: false);
            Record(next);
        }
        next.Synapses[index] = entry with
        {
            Status = refused.Length == 0 ? SynapseStatus.Written : SynapseStatus.Rejected,
            Errors = refused,
            ElapsedMs = elapsedMs,
            Attempts = attempts,
            Base = baseSynapse,
            Hash = hash ?? entry.Hash,
            Source = null,
        };
        DigitalBrain.Signal outcome = refused.Length == 0
            ? new SynapseWritten(entry.Path!, hash ?? entry.Hash ?? "", elapsedMs, attempts)
            : new SynapseRejected(entry.Sentence, refused);
        var replacedProgram = next.ProgramRevision != State.ProgramRevision;
        await Save(next, new AppChanged(Name(), next.Version, next.Status), outcome);
        if (replacedProgram) { await Publish(new PresentationChanged(Key, next.ProgramRevision, 0)); }
        return Describe(State);
    }

    // The bundle's rules judge the would-be request before it is published; the first rejection wins.
    // The sandbox picks the rules out of the program's shared classes; no program means no rules. Rules
    // fail closed: a program that declares one is rejected when no sandbox can run it.
    private async Task<Verdict?> Rejection(AppRequested requested)
    {
        var program = Sources(State);
        if (program.Count == 0) { return null; }
        if (ServiceProvider.GetService<IScriptSandbox>() is not { } sandbox)
        { return AppRules.Declared(program) ? Verdict.Reject("no sandbox") with { Rule = "rules" } : null; }
        var verdicts = await sandbox.Judge(BrainId(), program, requested, CancellationToken.None);
        return verdicts.FirstOrDefault(verdict => !verdict.Allowed);
    }

    public Task<RetainedRequest[]> RetainedRequests() => Task.FromResult(State.RequestRetention);

    public async Task Restore()
    {
        if (State.MigrationPaused || State.Manifest is null || Runtime is not { } runtime) { return; }
        var sources = Sources(State);
        if (!HasProgram(State)) { return; }
        var configuration = Configuration(State);
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        var principal = State.Principal ?? await HostCaller.ForBrain(ServiceProvider, BrainId());
        if (principal is not null) { CallerContextStamper.Stamp(principal); }
        else { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
        try
        {
            // Reassign the source on activation so a new host's contracts replace old calls, even when the app's version did not change.
            await runtime.Deploy(Key, sources, configuration, CancellationToken.None);
        }
        catch (Exception error)
        {
            ServiceProvider.GetRequiredService<ILogger<AppNeuron>>().LogError(error,
                "Could not restore the program of app {App} against the current contracts.", Key);
        }
        finally
        {
            if (inherited is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { RequestContext.Set(CallerContextStamper.RequestContextKey, inherited); }
        }
    }

    private IAppRuntime? Runtime => SelectRuntime(State);

    private IAppRuntime? SelectRuntime(AppState state)
    {
        var bundle = CurrentBundle(state);
        return runtimes.FirstOrDefault(r => r.Accepts(bundle)) ?? runtimes.FirstOrDefault();
    }

    private AppBundle CurrentBundle(AppState state)
    {
        state.Settings.TryGetValue(InProcessAppRuntime.CompiledTypeKey, out var compiledType);
        var prebuilt = InProcessAppRuntime.ExtractPrebuiltBehaviors(new(state.Files, StringComparer.Ordinal), new(state.Settings, StringComparer.Ordinal));
        return new AppBundle(
            Name(),
            state.Version,
            state.Manifest ?? new("", "", [], [], []),
            state.Spec,
            new(state.Files, StringComparer.Ordinal),
            prebuilt.Count > 0 ? prebuilt : null,
            compiledType);
    }

    private static bool HasProgram(AppState state)
        => state.Files.Keys.Any(AppFiles.IsProgram)
            || state.Files.Keys.Any(static k => k.EndsWith(".dll.base64", StringComparison.Ordinal))
            || (state.Settings.TryGetValue(InProcessAppRuntime.CompiledTypeKey, out var ct) && !string.IsNullOrWhiteSpace(ct))
            || (state.Settings.TryGetValue(InProcessAppRuntime.PrebuiltBehaviorsJsonKey, out var pb) && !string.IsNullOrWhiteSpace(pb));

    private static Dictionary<string, string> Sources(AppState state)
        => state.Files.Where(file => AppFiles.IsProgram(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);

    // The whole program, every time: the runtime decides what changed. No program means nothing runs.
    private async Task Deploy()
    {
        if (State.MigrationPaused) { return; }
        await RecordFeature();
        if (Runtime is not { } runtime) { return; }
        var sources = Sources(State);
        if (!HasProgram(State)) { await runtime.Retire(Key, CancellationToken.None); return; }
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        var principal = State.Principal ?? await HostCaller.ForBrain(ServiceProvider, BrainId());
        if (principal is not null) { CallerContextStamper.Stamp(principal); }
        try
        {
            await runtime.Deploy(Key, sources, Configuration(State), CancellationToken.None);
        }
        finally
        {
            if (inherited is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { RequestContext.Set(CallerContextStamper.RequestContextKey, inherited); }
        }
    }

    // Every program assignment lands in the kernel as this brain's feature, whatever its source:
    // the features folder, the Marketplace or the Creator. Owner is where it came from.
    private async Task RecordFeature()
    {
        if (ServiceProvider.GetService<IKernel>() is not { } kernel || State.Manifest is null || BrainId().Length == 0) { return; }
        var feature = new Feature(Name(), State.Version, State.Origin?.Publisher ?? BrainId());
        await kernel.LoadFeature(BrainId(), feature, FeatureTypes.Of(Sources(State), feature.Provider));
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
        values["PublicOrigin"] = configuration?[IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ConnectionsPublicOriginKey] ?? "";
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

    private void Record(AppState next)
    {
        var record = new AppVersionRecord(next.Version, clock.GetUtcNow(), next.Manifest!, next.Spec, next.Files);
        // One entry per version: a version saved again replaces its entry.
        next.History = [.. next.History.Where(held => held.Version != next.Version).TakeLast(MaxHistory - 1), record];
    }

    private bool Replayed(Guid operationId) => State.HasReceipt(operationId);

    private static void Receipt(AppState next, Guid operationId)
    {
        next.Receipts = [.. next.Receipts.Append(new OperationReceipt(operationId)).TakeLast(MaxReceipts)];
    }

    private Task RequireCreated(string operation)
        => Check(operation, new AppExists(Name(), State.Manifest is not null));

    // The publish gate: every script in the bundle compiles against the installed contracts. A host
    // without a script compiler cannot run apps either, so it has nothing to refuse.
    private Task RequireCompiles() => RequireCompiles(State);

    private async Task RequireCompiles(AppState state)
    {
        var scripts = state.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        if (scripts.Count == 0) { return; }
        // The declaration compiles with the program it describes.
        scripts[AppFiles.DeclarationPath] = AppDeclaration.Write(new(Name(), state.Version, state.Manifest!, state.Spec));
        var sandbox = ServiceProvider.GetService<IScriptSandbox>();
        if (sandbox is null) { return; }
        ScriptCompilationCheck check;
        try { check = await sandbox.Compile(scripts, CancellationToken.None); }
        catch (InvalidOperationException) { return; }
        if (check.Success) { return; }
        // build outcome, caught by Build
        throw new InvalidOperationException($"Version {State.Version} does not compile: "
            + string.Join("; ", check.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}")));
    }

    private string Name()
    {
        var key = Key;
        var marker = key.LastIndexOf("/apps/", StringComparison.Ordinal);
        return marker >= 0 ? key[(marker + "/apps/".Length)..] : key;
    }

    // The key is {brain}/apps/{name}; the brain scope is ambient in the address.
    private string BrainId()
    {
        var key = Key;
        var marker = key.LastIndexOf("/apps/", StringComparison.Ordinal);
        return marker >= 0 ? key[..marker] : "";
    }

    private static string Bump(string version, bool minor)
    {
        var parts = version.Split('.');
        return minor ? $"{parts[0]}.{int.Parse(parts[1]) + 1}.0"
                     : $"{parts[0]}.{parts[1]}.{int.Parse(parts[2]) + 1}";
    }

    private static void AdvanceProgram(AppState next)
    {
        next.ProgramRevision = checked(next.ProgramRevision + 1);
        next.Presentation = null;
    }

    public async Task<AppSnapshot> SaveRuntimeState(string stateJson, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        if (expectedRevision < State.RuntimeStateRevision)
        {
            // The runner sees the revision it gets back and logs that its state was not kept.
            ServiceProvider.GetRequiredService<ILogger<AppNeuron>>().LogWarning("App {App} ignored a state save at revision {Expected}; it holds revision {Current}", Key, expectedRevision, State.RuntimeStateRevision);
            return Describe(State);
        }
        var next = State.Clone();
        next.RuntimeStateJson = stateJson;
        next.RuntimeStateRevision = expectedRevision + 1;
        await Save(next, new AppChanged(Name(), next.Version, next.Status));
        return Describe(next);
    }

    private Task Persist(AppState next)
        => next.ProgramRevision == State.ProgramRevision
            ? Save(next, new AppChanged(Name(), next.Version, next.Status))
            : Save(next, new AppChanged(Name(), next.Version, next.Status),
                new PresentationChanged(Key, next.ProgramRevision, 0));

    private AppSnapshot Describe(AppState state) => new(
        Name(),
        state.Manifest ?? new("", "", [], [], []),
        state.Spec,
        new(state.Files, StringComparer.Ordinal),
        new(state.Settings, StringComparer.Ordinal),
        new(state.Connections, StringComparer.Ordinal),
        state.Version,
        state.Status,
        [.. state.Activity],
        [.. state.History.Select(record => new AppVersionSummary(record.Version, record.SavedAt))],
        state.PublishedVersion,
        state.Origin,
        state.Edited,
        [.. state.Synapses],
        state.Installation,
        state.Presentation,
        state.ProgramRevision,
        state.MigrationTarget,
        state.RuntimeStateJson,
        state.RuntimeStateRevision);
}
