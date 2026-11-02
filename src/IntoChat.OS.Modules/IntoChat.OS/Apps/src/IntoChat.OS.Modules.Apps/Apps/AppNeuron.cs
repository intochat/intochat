using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flame;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private bool _cleared;
    // The state being saved: Read interleaves with a turn, and a reader that heard the change (its signal is journalled before the
    // base sets State) must see it, as readers of the grain's store did (06-08).
    private AppState? _saving;

    private AppState Snapshot => _saving ?? (_cleared ? Cleared : State);
    // Whether the app has a record: created apps always carry a manifest.
    private bool Exists => Snapshot.Manifest is not null;
    private IServiceProvider ServiceProvider => Context.Services;
    // The os full key "{brain}/apps/{name}" the app was addressed by as a grain.
    private string Key => $"{Id.Brain.Value}/{Id.Key}";

    // Every activation reconciles an interrupted turn, then queues the program's restore as its own turn.
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Interrupt();
        if (!Snapshot.MigrationPaused && Snapshot.Manifest is not null && Snapshot.Files.Keys.Any(AppFiles.IsProgram))
        { _ = Get<IAppDeploy>(Id).Restore(); }
    }

    protected override void OnCommitted() => _cleared = false;

    private Task Save(AppState next, DigitalBrain.Signal first, DigitalBrain.Signal second, DigitalBrain.Signal third) => Save(next, [first, second, third]);

    private new Task Save(AppState next, DigitalBrain.Signal changed, DigitalBrain.Signal? acted = null)
        => Save(next, acted is null ? [changed] : [changed, acted]);

    private new async Task Save(AppState next, IReadOnlyList<DigitalBrain.Signal> signals)
    {
        _saving = next;
        try { await base.Save(next, signals); }
        finally { _saving = null; }
    }

    // Deleting the record ends the app's existence (the kernel retires the instance), then says so.
    private async Task Clear(DigitalBrain.Signal changed)
    {
        await Context.State.Clear();
        _cleared = true;
        await Publish(changed);
    }

    // Another app of this brain, by its os key.
    private DigitalBrain.NeuronId AppId(string key) => new(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, key[(Id.Brain.Value.Length + 1)..]);

    // The platform-wide Marketplace is still a grain outside every brain.
    private IMarketplace Marketplace => SiloNeurons.Get<IMarketplace>(Context.Services, IMarketplace.Key);
    private const int MaxHistory = 20;
    private const int MaxActivity = 50;
    private const int MaxPayloadLength = 64 * 1024;
    private const int MaxReceipts = 64;
    private const int RetainedCapacity = 64;
    private const int MaxSynapses = 64;
    private const int MaxSentenceLength = 4000;
    internal const int MaxFocus = 16;
    internal const int MaxFocusKeyLength = 512;

    public Task<AppSnapshot> Read() => Task.FromResult(Describe(Snapshot));

    public async Task<AppSnapshot> Create(AppManifest manifest, string spec)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Snapshot.Manifest is not null)
        { throw new InvalidOperationException($"You already have an app named {Name()}. Open it in the Creator, or describe this one differently."); }
        var next = Snapshot.Clone();
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
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> SaveSpec(string spec, Guid operationId)
    {
        RequireRunningProgram();
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.Spec = spec;
        next.Version = Bump(next.Version, minor: true);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId)
    {
        RequireRunningProgram();
        RequireCreated();
        ArgumentNullException.ThrowIfNull(manifest);
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
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
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId)
    {
        RequireCreated();
        RequireRunningProgram();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (path == AppFiles.DeclarationPath) { throw new ArgumentException($"{AppFiles.DeclarationPath} is the app's declaration: change it through SaveManifest and SaveSpec."); }
        if (!AppFiles.IsProgram(path)) { throw new ArgumentException("A program file is a .cs file: a synapse (top-level statements) or a shared class."); }
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.PutFile(path, source);
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
        if (requested >= 0) { next.Synapses[requested] = next.Synapses[requested] with { Path = path, Hash = hashes?.GetValueOrDefault(path) }; }
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteFile(string path, string source, Guid operationId)
    {
        RequireRunningProgram();
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (path.EndsWith(".cs", StringComparison.Ordinal)) { throw new ArgumentException("Programs are written through WriteBehavior; app.cs through SaveManifest and SaveSpec."); }
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.PutFile(path, source);
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections)
    {
        RequireRunningProgram();
        RequireCreated();
        // A storage hook vetoes a binding change its data depends on, before anything changes.
        foreach (var (slot, id) in connections ?? [])
        {
            if (Snapshot.Connections.TryGetValue(slot, out var current) && current == id) { continue; }
            foreach (var hook in storageHooks)
            { await hook.RequireRebindable(Snapshot.BrainScopeId ?? "", Key, slot, CancellationToken.None); }
        }
        var next = Snapshot.Clone();
        foreach (var (name, value) in settings ?? []) { next.PutSetting(name, value); }
        foreach (var (slot, id) in connections ?? []) { next.PutConnection(slot, id); }
        AdvanceProgram(next);
        await Persist(next);
        // The running program reads its settings from the deployment configuration, so a
        // configure hands it the whole thing again.
        await Deploy();
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Rollback(string version)
    {
        RequireRunningProgram();
        RequireCreated();
        var record = Snapshot.History.FirstOrDefault(item => item.Version == version)
            ?? throw new ArgumentException($"No version {version}. This app has: {string.Join(", ", Snapshot.History.Select(item => item.Version))}.");
        var next = Snapshot.Clone();
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
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> BeginBuild(string? instruction)
    {
        RequireRunningProgram();
        RequireCreated();
        if (Snapshot.Status == AppStatus.Building
            && clock.GetUtcNow() - Snapshot.BuildStartedAt < TimeSpan.FromSeconds(options.Value.BuildFreshForSeconds))
        { throw new InvalidOperationException("The Builder is already working on this app."); }
        var next = Snapshot.Clone();
        next.Status = AppStatus.Building;
        next.BuildAttempt = Guid.NewGuid();
        next.BuildStartedAt = clock.GetUtcNow();
        next.Activity = string.IsNullOrWhiteSpace(instruction) ? ["build started"] : ["build started: " + instruction.Trim()];
        await Persist(next);
        return Describe(Snapshot);
    }

    private static readonly string[] LiveStates = ["working", "reading", "searching", "accepted"];

    // A turn-in-progress activity (not listening or waiting, which an idle app on a live synapse keeps) retained across a deactivation belongs to a turn that died with the process; a late
    // listener must not see it as current. Do not republish the activity; reconcile its reserved
    // presentation flags in the same durable write so the visible presence is not left running.
    // 06-08: a late listener is replayed the app's newest journalled AppActivity (the runtime's retention), so the reconciled
    // activity is journalled with the reconciled flags; live listeners hear the interruption too.
    internal Task Interrupt()
    {
        if (!Exists || Snapshot.LastActivity is not { State: var state } retained
            || !LiveStates.Contains(state, StringComparer.Ordinal))
        { return Task.CompletedTask; }
        var next = Snapshot.Clone();
        next.ActivitySequence++;
        var interrupted = retained with { State = "attention", Detail = "interrupted" };
        next.LastActivity = interrupted;
        var presentation = ApplyActivityPresentation(next, "attention");
        return presentation is null ? Save(next, interrupted) : Save(next, interrupted, presentation);
    }

    public Task<Guid> CurrentBuildAttempt() => Task.FromResult(Snapshot.BuildAttempt);

    public Task Progress(string state, string detail) => Progress(state, detail, []);

    public async Task Progress(string state, string detail, Dictionary<string, string> blackboard)
    {
        // A background writer may report after deletion; activity must not recreate its app record.
        if (Snapshot.Manifest is null) { return; }
        if (!AppActivity.States.Contains(state, StringComparer.Ordinal))
        { throw new ArgumentException($"'{state}' is not an activity state: {string.Join(", ", AppActivity.States)}.", nameof(state)); }
        detail = (detail ?? "").Trim();
        // In-memory update without Snapshot.Clone() or Save() disk flushes
        var building = Snapshot.Status == AppStatus.Building && detail.Length > 0
            && Snapshot.Activity.LastOrDefault() != detail;
        if (building) { State.Activity = [.. State.Activity.TakeLast(MaxActivity - 1), detail]; }
        var activity = new AppActivity(Name(), state, detail);
        State.ActivitySequence++;
        State.LastActivity = activity;
        var presentation = ApplyActivityPresentation(State, state, blackboard);
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
        if (extra is { Count: > 0 })
        {
            // The sphere is published by another behavior. Until it exists, the activity still commits
            // and keys that have nowhere to go wait for a later write.
            if (declared is null) { extra = null; }
            else
            {
                foreach (var key in extra.Keys)
                {
                    if (FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal)
                        || !declared.Contains(key, StringComparer.Ordinal))
                    { throw new ArgumentException($"'{key}' is not a script-writable declared blackboard key.", nameof(extra)); }
                }
            }
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
        if (Snapshot.MigrationPaused) { return Describe(Snapshot); }
        if (Snapshot.Status != AppStatus.Building || attempt != Snapshot.BuildAttempt) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        if (error is not null || files is not { Count: > 0 })
        {
            next.Status = AppStatus.Failed;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "build failed: " + (error ?? "the Builder produced no files")];
            await Persist(next);
            return Describe(Snapshot);
        }
        if (files.ContainsKey(AppFiles.DeclarationPath))
        {
            next.Status = AppStatus.Failed;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), $"build failed: {AppFiles.DeclarationPath} belongs to the Author, not the Builder"];
            await Persist(next);
            return Describe(Snapshot);
        }
        var firstBuild = Snapshot.Files.Count == 0;
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
            var failed = Snapshot.Clone();
            failed.Status = AppStatus.Failed;
            failed.Activity = [.. failed.Activity.TakeLast(MaxActivity - 1), "build failed: " + failure.Message];
            await Persist(failed);
        }
        return Describe(Snapshot);
    }

    public async Task<AppBundle> Export()
    {
        RequireCreated();
        await RequireCompiles();
        var manifest = Snapshot.Manifest!;
        return new AppBundle(Name(), Snapshot.Version, manifest, Snapshot.Spec,
            new(Snapshot.Files, StringComparer.Ordinal));
    }

    public async Task<AppSnapshot> Publish()
    {
        RequireRunningProgram();
        RequireCreated();
        await RequireCompiles();
        if (!CallerContextStamper.TryGet(out var caller))
        { throw new InvalidOperationException("Publishing needs a signed-in publisher."); }
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != BrainId())
        { throw new UnauthorizedAccessException("Only the brain that owns an app can publish it."); }
        // A listing is one app: the same name from another of the publisher's brains is refused,
        // never a silent takeover of what installers already trust.
        if (await Marketplace.Read(caller.PersonId, Name()) is { } listed
            && listed.Source.Scope() != BrainId())
        { throw new InvalidOperationException($"{caller.PersonId}/{Name()} is already published from another brain."); }
        var next = Snapshot.Clone();
        var manifest = next.Manifest!;
        next.PublishedVersion = next.Version;
        next.Published = new AppVersionRecord(next.Version, clock.GetUtcNow(), manifest, next.Spec, next.Files);
        next.Publisher = caller.PersonId;
        var releases = Releases();
        var release = Release(next.Published, caller.PersonId, new AppSource.Brain(caller.AccountId, caller.BrainId));
        var existing = releases.FirstOrDefault(item => item.Bundle.Version == next.Version);
        if (existing is not null && existing.Publication.Reference != release.Publication.Reference)
        { throw new InvalidOperationException("This version has already been published with different content. Create a new version."); }
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
        return Describe(Snapshot);
    }

    public Task<AppBundle?> ExportVersion(string version)
    {
        var record = Snapshot.History.LastOrDefault(item => item.Version == version);
        return Task.FromResult(record is null ? null
            : new AppBundle(Name(), record.Version, record.Manifest, record.Spec, new(record.Files, StringComparer.Ordinal)));
    }

    public Task<AppBundle> ExportPublished()
    {
        if (Snapshot.Published is not { } published)
        { throw new InvalidOperationException($"{Name()} has no published version."); }
        return Task.FromResult(new AppBundle(Name(), published.Version, published.Manifest, published.Spec,
            new(published.Files, StringComparer.Ordinal)));
    }

    public async Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        RequireBundleFor(bundle);
        if (installation is not null)
        {
            ValidateInstallation(bundle, origin, installation);
            if (Snapshot.Manifest is not null && Snapshot.Installation == installation)
            {
                if (Snapshot.Edited || Snapshot.Status == AppStatus.Live) { return Describe(Snapshot); }
                var held = new AppBundle(Name(), Snapshot.Version, Snapshot.Manifest, Snapshot.Spec, new(Snapshot.Files, StringComparer.Ordinal));
                if (held.Version != installation.Publication.Version || AppPublicationDigest.Of(held) != installation.Publication.Digest)
                { throw new InvalidOperationException("This installed copy has changed; it cannot resume the original install."); }
                return await Land(Snapshot.Clone());
            }
        }
        if (Snapshot.Manifest is not null)
        { throw new InvalidOperationException($"You already have an app named {Name()}."); }
        var next = Snapshot.Clone();
        next.BrainScopeId = BrainId();
        next.Principal = CallerContextStamper.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = bundle.Files;
        next.Settings = bundle.Manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
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
        RequireCreated();
        RequireBundleFor(bundle);
        // A retried update at the version already held, with the same program, changes nothing.
        if (bundle.Version == Snapshot.Version && bundle.Spec == Snapshot.Spec
            && bundle.Files.Count == Snapshot.Files.Count
            && bundle.Files.All(file => Snapshot.Files.TryGetValue(file.Key, out var held) && held == file.Value)
            && Snapshot.Status == AppStatus.Live)
        {
            if (origin is null || (origin with { Version = bundle.Version } == Snapshot.Origin && !Snapshot.Edited)) { return Describe(Snapshot); }
            // The same program under a different shipped package is a different running program: it is assigned.
            var repackaged = origin.PackageDigest != Snapshot.Origin?.PackageDigest || (Snapshot.Edited && origin.PackageDigest is not null);
            var attached = Snapshot.Clone();
            Adopt(attached, origin);
            await Persist(attached);
            await RecordFeature();
            if (repackaged) { await Deploy(); }
            return Describe(Snapshot);
        }
        var next = Snapshot.Clone();
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = bundle.Files;
        foreach (var setting in bundle.Manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.PutSetting(setting.Name, setting.Default); } }
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
            var failed = Snapshot.Clone();
            failed.Status = AppStatus.Failed;
            failed.Activity = [.. failed.Activity.TakeLast(MaxActivity - 1), "deploy failed: " + failure.Message];
            await Persist(failed);
            throw;
        }
        var live = Snapshot.Clone();
        live.Status = AppStatus.Live;
        await Persist(live);
        return Describe(Snapshot);
    }

    private void RequireBundleFor(AppBundle bundle)
    {
        if (bundle.Name != Name() || string.IsNullOrWhiteSpace(bundle.Name)
            || bundle.Name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
        { throw new InvalidDataException($"'{bundle.Name}' is not a valid app name: lowercase letters, digits and dashes, matching the key."); }
        if (bundle.Version.Split('.') is not { Length: 3 } parts || parts.Any(part => !part.All(char.IsAsciiDigit) || part.Length == 0))
        { throw new InvalidDataException($"'{bundle.Version}' is not a version of the form major.minor.patch."); }
    }

    public async Task RecordOrigin(AppOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        // Recording the publisher's origin marks a copy unedited and shipped, which every ShippedApps.Is guard trusts:
        // an app behind the proxy (itself included) must never write its own.
        if (CallerContextStamper.TryGet(out var caller) && caller.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Only the kernel or a person records where an app came from."); }
        RequireCreated();
        var next = Snapshot.Clone();
        next.Origin = origin;
        next.Edited = false;
        await Persist(next);
        await RecordFeature();
    }


    public async Task<AppSnapshot> SaveToSource()
    {
        RequireRunningProgram();
        RequireCreated();
        if (ServiceProvider.GetService<IHostEnvironment>() is not { } environment || !environment.IsDevelopment())
        { throw new InvalidOperationException("Save to source is only available in development."); }
        if (Snapshot.Origin is not { Source: AppSource.Shipped } origin)
        { throw new InvalidOperationException($"{Name()} is not a copy of a feature from the features folder, so it has no source to save to."); }
        var catalog = ServiceProvider.GetRequiredService<FeatureCatalog>();
        var exported = await Export();
        var shipped = await catalog.Find(origin.Publisher, origin.Name);
        var version = Bump(Newest(exported.Version, shipped?.Version ?? exported.Version), minor: false);
        var saved = await catalog.SaveToSource(origin.Publisher, exported with { Name = origin.Name, Version = version });
        var next = Snapshot.Clone();
        next.Version = saved.Version;
        next.Origin = origin with { Version = saved.Version };
        next.Edited = false;
        Record(next);
        await Persist(next);
        return Describe(Snapshot);
    }

    private static string Newest(string left, string right)
        => Version.Parse(left) >= Version.Parse(right) ? left : right;

    public async Task Delete()
    {
        RequireRunningProgram();
        if (Snapshot.Manifest is null) { return; }
        var key = Key;
        // Only this app's own listing comes down with it.
        if (Snapshot.Publisher is { } publisher
            && await Marketplace.Read(publisher, Name()) is { } listed
            && listed.Source.Scope() == BrainId())
        { await Marketplace.Remove(publisher, Name()); }
        // Everything the app owns goes with it: its program, then its storage.
        if (Runtime is { } runtime) { await runtime.Retire(key, CancellationToken.None); }
        if (ServiceProvider.GetService<IKernel>() is { } kernel) { await kernel.UnloadFeature(BrainId(), Name()); }
        foreach (var hook in storageHooks) { await hook.OnRetired(Snapshot.BrainScopeId ?? "", key, CancellationToken.None); }
        await RemoveAppPrincipal();
        // Clearing the record ends the app's existence: the kernel retires the instance.
        var gone = new AppState();
        await Clear(new AppChanged(Name(), gone.Version, gone.Status));
    }

    private async Task Reopen()
    {
        foreach (var hook in storageHooks) { await hook.OnCreated(BrainId(), Key, CancellationToken.None); }
    }

    public async Task<Guid> Request(string operation, string input)
    {
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(input);
        if (!Snapshot.Manifest!.Operations.Any(candidate => candidate.Name == operation))
        {
            throw new ArgumentException(
                $"{operation} is not an operation of {Name()}. It offers: {string.Join(", ", Snapshot.Manifest.Operations.Select(candidate => candidate.Name))}.");
        }
        if (input.Length > MaxPayloadLength) { throw new ArgumentException("A request input is at most 64 KiB."); }
        var id = Guid.NewGuid();
        if (Snapshot.MigrationTarget is { } target)
        {
            await Get<IAppMigration>(AppId(target)).Accept(operation, input, id);
            return id;
        }
        var requested = new AppRequested(operation, input, id);
        if (await Rejection(requested) is { } rejected)
        {
            await Publish(new AppRejected(operation, rejected));
            await Progress("attention", $"{rejected.Rule} refused {operation}: {rejected.Reason}");
            throw new ArgumentException($"{rejected.Rule} refused {operation}: {rejected.Reason}");
        }
        // A request is a fact for the app's synapses; it is published for whoever is watching and kept
        // for whoever subscribes later, so a synapse that starts after the request still gets it.
        var next = Snapshot.Clone();
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
        RequireRunningProgram();
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        if (sentence.Length > MaxSentenceLength) { throw new ArgumentException($"Say the synapse in at most {MaxSentenceLength} characters."); }
        string[] keys = [.. (focus ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal)];
        if (keys.Length > MaxFocus) { throw new ArgumentException($"Focus on at most {MaxFocus} neurons."); }
        if (keys.Any(key => key.Length > MaxFocusKeyLength)) { throw new ArgumentException($"A focus key is at most {MaxFocusKeyLength} characters."); }
        if (Replayed(operationId) || Snapshot.Synapses.Any(synapse => synapse.OperationId == operationId)) { return Describe(Snapshot); }
        if (ServiceProvider.GetService<IScriptSandbox>()?.CanRun != true) { throw new InvalidOperationException(AppAuthoringPolicy.SandboxMissing); }
        var next = Snapshot.Clone();
        next.Synapses = [.. next.Synapses.TakeLast(MaxSynapses - 1),
            new AppSynapse(operationId, sentence.Trim(), keys, SynapseStatus.Requested, clock.GetUtcNow(), [])];
        await Persist(next);
        // The writer runs on the work queue, outside this turn, and calls back as an outside caller.
        ServiceProvider.GetRequiredService<ReactionWriter>().Enqueue(Key, operationId, sentence.Trim(), keys);
        return Describe(Snapshot);
    }

    public async Task<StagedProgram> Stage(Guid operationId, string path, string source)
    {
        RequireCreated();
        RequireRunningProgram();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (path == AppFiles.DeclarationPath) { throw new ArgumentException($"{AppFiles.DeclarationPath} is the app's declaration: change it through SaveManifest and SaveSpec."); }
        if (!AppFiles.IsProgram(path)) { throw new ArgumentException("A program file is a .cs file: a synapse (top-level statements) or a shared class."); }
        var index = Array.FindIndex(Snapshot.Synapses, synapse => synapse.OperationId == operationId);
        if (index < 0 || Snapshot.Synapses[index].Status != SynapseStatus.Requested)
        { throw new InvalidOperationException("No requested synapse waits under that operation id."); }
        var sources = Sources(Snapshot);
        sources[path] = source;
        var entry = Snapshot.Synapses[index];
        if (entry.Path != path || entry.Source != source)
        {
            var next = Snapshot.Clone();
            next.Synapses[index] = entry with { Path = path, Source = source };
            AdvanceProgram(next);
            await Persist(next);
        }
        return new(sources, Configuration(Snapshot), Snapshot.Principal);
    }

    // A deleted app has no files, so a writer that outlived it deploys an empty program, which retires the runtime.
    public async Task<StagedProgram> Program()
    {
        if (!Exists || Snapshot.MigrationPaused) { return new([], Configuration(Snapshot), Snapshot.Principal); }
        var next = Snapshot.Clone();
        AdvanceProgram(next);
        await Persist(next);
        return new(Sources(Snapshot), Configuration(Snapshot), Snapshot.Principal);
    }

    // A deleted app holds no requests, so a late Unsettle answers its empty snapshot and writes nothing.
    public async Task<AppSnapshot> Unsettle(Guid operationId, string reason, long elapsedMs, int attempts, string? baseSynapse)
    {
        var index = Array.FindIndex(Snapshot.Synapses, synapse => synapse.OperationId == operationId);
        if (index < 0 || Snapshot.Synapses[index].Status != SynapseStatus.Requested) { return Describe(Snapshot); }
        var entry = Snapshot.Synapses[index];
        var next = Snapshot.Clone();
        next.Synapses[index] = entry with { Status = SynapseStatus.Unsettled, Errors = [reason], ElapsedMs = elapsedMs, Attempts = attempts, Base = baseSynapse };
        await Save(next, new AppChanged(Name(), next.Version, next.Status), new SynapseRejected(entry.Sentence, [reason]));
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> CompleteSynapse(Guid operationId, string[] errors, long elapsedMs, int attempts, string? baseSynapse = null, string? hash = null)
    {
        if (Snapshot.MigrationPaused) { return Describe(Snapshot); }
        var index = Array.FindIndex(Snapshot.Synapses, synapse => synapse.OperationId == operationId);
        if (index < 0 || Snapshot.Synapses[index].Status != SynapseStatus.Requested) { return Describe(Snapshot); }
        var entry = Snapshot.Synapses[index];
        string[] refused = [.. errors ?? []];
        // A synapse WriteBehavior deployed (nothing staged) is Written, even when its caller gave up waiting on it.
        if (entry.Source is null && entry.Path is { } deployed && Snapshot.Files.ContainsKey(deployed)) { refused = []; }
        if (refused.Length == 0 && entry.Path is null) { refused = ["The writer reported a synapse it never wrote."]; }
        var next = Snapshot.Clone();
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
        var replacedProgram = next.ProgramRevision != Snapshot.ProgramRevision;
        await Save(next, new AppChanged(Name(), next.Version, next.Status), outcome);
        if (replacedProgram) { await Publish(new PresentationChanged(Key, next.ProgramRevision, 0)); }
        return Describe(Snapshot);
    }

    // The bundle's rules judge the would-be request before it is published; the first rejection wins.
    // The sandbox picks the rules out of the program's shared classes; no program means no rules. Rules
    // fail closed: a program that declares one is rejected when no sandbox can run it.
    private async Task<Verdict?> Rejection(AppRequested requested)
    {
        var program = Sources(Snapshot);
        if (program.Count == 0) { return null; }
        if (ServiceProvider.GetService<IScriptSandbox>() is not { } sandbox)
        { return AppRules.Declared(program) ? Verdict.Reject("no sandbox") with { Rule = "rules" } : null; }
        var verdicts = await sandbox.Judge(BrainId(), program, requested, CancellationToken.None);
        return verdicts.FirstOrDefault(verdict => !verdict.Allowed);
    }

    public Task<RetainedRequest[]> RetainedRequests() => Task.FromResult(Snapshot.RequestRetention);

    public async Task Restore()
    {
        if (Snapshot.MigrationPaused || Snapshot.Manifest is null || Runtime is not { } runtime) { return; }
        var sources = Sources(Snapshot);
        if (sources.Count == 0) { return; }
        var configuration = Configuration(Snapshot);
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        var principal = Snapshot.Principal ?? await HostCaller.ForBrain(ServiceProvider, BrainId());
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

    private IAppRuntime? Runtime => runtimes.FirstOrDefault();

    private static Dictionary<string, string> Sources(AppState state)
        => state.Files.Where(file => AppFiles.IsProgram(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);

    // The whole program, every time: the runtime decides what changed. No program means nothing runs.
    private async Task Deploy()
    {
        if (Snapshot.MigrationPaused) { return; }
        await RecordFeature();
        if (Runtime is not { } runtime) { return; }
        var sources = Sources(Snapshot);
        if (sources.Count == 0) { await runtime.Retire(Key, CancellationToken.None); return; }
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        var principal = Snapshot.Principal ?? await HostCaller.ForBrain(ServiceProvider, BrainId());
        if (principal is not null) { CallerContextStamper.Stamp(principal); }
        try
        {
            await runtime.Deploy(Key, sources, Configuration(Snapshot), CancellationToken.None);
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
        if (ServiceProvider.GetService<IKernel>() is not { } kernel || Snapshot.Manifest is null || BrainId().Length == 0) { return; }
        var feature = new Feature(Name(), Snapshot.Version, Snapshot.Origin?.Publisher ?? BrainId());
        await kernel.LoadFeature(BrainId(), feature, FeatureTypes.Of(Sources(Snapshot), feature.Provider));
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

    private bool Replayed(Guid operationId) => Snapshot.Receipts.Any(receipt => receipt.OperationId == operationId);

    private static void Receipt(AppState next, Guid operationId)
    {
        next.Receipts = [.. next.Receipts.Append(new OperationReceipt(operationId, "", "")).TakeLast(MaxReceipts)];
    }

    private void RequireCreated()
    {
        if (Snapshot.Manifest is null)
        { throw new InvalidOperationException($"No app named {Name()} exists in this brain."); }
    }

    private void RequireRunningProgram()
    {
        if (Snapshot.MigrationPaused) { throw new InvalidOperationException("The app program is paused for migration."); }
    }

    // The publish gate: every script in the bundle compiles against the installed contracts. A host
    // without a script compiler cannot run apps either, so it has nothing to refuse.
    private Task RequireCompiles() => RequireCompiles(Snapshot);

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
        throw new InvalidOperationException($"Version {Snapshot.Version} does not compile: "
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
        if (expectedRevision < Snapshot.RuntimeStateRevision)
        {
            return Describe(Snapshot);
        }
        var next = Snapshot.Clone();
        next.RuntimeStateJson = stateJson;
        next.RuntimeStateRevision = expectedRevision + 1;
        await Save(next, new AppChanged(Name(), next.Version, next.Status));
        return Describe(next);
    }

    private Task Persist(AppState next)
        => next.ProgramRevision == Snapshot.ProgramRevision
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

