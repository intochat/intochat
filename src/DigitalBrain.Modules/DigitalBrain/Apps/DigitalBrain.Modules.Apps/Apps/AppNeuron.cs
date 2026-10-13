using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Apps;

// The app: the only durable thing in the module. Manifest, spec, files, connections,
// semver history, status and activity all live here; the Builder is a cache that writes
// its results back in.
[GrainType(DigitalBrain.Framework.AppGrainType.Name)]
internal sealed partial class AppNeuron(
    [PersistentState("app2", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<AppState> store,
    TimeProvider clock, IOptions<AppsOptions> options,
    IEnumerable<IAppRuntime> runtimes, IEnumerable<IAppStorageHook> storageHooks)
    : Neuron<AppState>(store), IApp, IAppDeploy, IRetainsSignals
{
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
        next.Principal = CallerContextStamper.TryGet(out var caller) ? caller : null;
        next.Spec = spec ?? "";
        next.Settings = manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        next.Version = "0.1.0";
        next.Status = AppStatus.Drafted;
        await Reopen();
        Record(next);
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> SaveSpec(string spec, Guid operationId)
    {
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
        RequireCreated();
        ArgumentNullException.ThrowIfNull(manifest);
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.Manifest = manifest.Validated();
        // Newly declared settings get their defaults; chosen values survive.
        foreach (var setting in manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.Settings[setting.Name] = setting.Default; } }
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
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (path == AppFiles.DeclarationPath) { throw new ArgumentException($"{AppFiles.DeclarationPath} is the app's declaration: change it through SaveManifest and SaveSpec."); }
        if (!AppFiles.IsProgram(path)) { throw new ArgumentException("A program file is a .cs file: a synapse (top-level statements) or a shared class."); }
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.Files[path] = source;
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        // The whole program goes to the runtime before anything is persisted: a write the
        // compiler refuses leaves the app's files exactly as they were.
        IReadOnlyDictionary<string, string>? hashes = null;
        if (Runtime is { } runtime)
        {
            var sources = Sources(next);
            RequireShape(sources);
            hashes = await runtime.Deploy(this.GetPrimaryKeyString(), sources, Configuration(next), CancellationToken.None);
        }
        // A fast-path write names its deployed synapse on the request; CompleteSynapse lands the outcome.
        var requested = Array.FindIndex(next.Synapses, synapse => synapse.OperationId == operationId && synapse.Status == SynapseStatus.Requested);
        if (requested >= 0) { next.Synapses[requested] = next.Synapses[requested] with { Path = path, Hash = hashes?.GetValueOrDefault(path) }; }
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteFile(string path, string source, Guid operationId)
    {
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (path.EndsWith(".cs", StringComparison.Ordinal)) { throw new ArgumentException("Programs are written through WriteBehavior; app.cs through SaveManifest and SaveSpec."); }
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.Files[path] = source;
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections)
    {
        RequireCreated();
        // A storage hook vetoes a binding change its data depends on, before anything changes.
        foreach (var (slot, id) in connections ?? [])
        {
            if (Snapshot.Connections.TryGetValue(slot, out var current) && current == id) { continue; }
            foreach (var hook in storageHooks)
            { await hook.RequireRebindable(Snapshot.BrainScopeId ?? "", this.GetPrimaryKeyString(), slot, CancellationToken.None); }
        }
        var next = Snapshot.Clone();
        foreach (var (name, value) in settings ?? []) { next.Settings[name] = value; }
        foreach (var (slot, id) in connections ?? []) { next.Connections[slot] = id; }
        await Persist(next);
        // The running program reads its settings from the deployment configuration, so a
        // configure hands it the whole thing again.
        await Deploy();
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Rollback(string version)
    {
        RequireCreated();
        var record = Snapshot.History.FirstOrDefault(item => item.Version == version)
            ?? throw new ArgumentException($"No version {version}. This app has: {string.Join(", ", Snapshot.History.Select(item => item.Version))}.");
        var next = Snapshot.Clone();
        next.Edited = true;
        next.Manifest = record.Manifest;
        next.Spec = record.Spec;
        next.Files = new(record.Files, StringComparer.Ordinal);
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        await Persist(next);
        // Programs the target version lacks stop with the rest: one program, assigned whole.
        await Deploy();
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> BeginBuild(string? instruction)
    {
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
    // listener must not see it as current. Retention only: nothing is published.
    internal Task Interrupt()
    {
        if (!Exists || Snapshot.ActivityRetention is not [{ Signal: AppActivity { State: var state } retained }]
            || !LiveStates.Contains(state, StringComparer.Ordinal))
        { return Task.CompletedTask; }
        var next = Snapshot.Clone();
        next.ActivitySequence++;
        next.ActivityRetention = [new RetainedSignal(next.ActivitySequence, retained with { State = "attention", Detail = "interrupted" })];
        return SaveQuietly(next);
    }

    public Task<Guid> CurrentBuildAttempt() => Task.FromResult(Snapshot.BuildAttempt);

    public async Task Progress(string state, string detail)
    {
        if (!AppActivity.States.Contains(state, StringComparer.Ordinal))
        { throw new ArgumentException($"'{state}' is not an activity state: {string.Join(", ", AppActivity.States)}.", nameof(state)); }
        detail = (detail ?? "").Trim();
        var next = Snapshot.Clone();
        var building = Snapshot.Status == AppStatus.Building && detail.Length > 0
            && Snapshot.Activity.LastOrDefault() != detail;
        if (building) { next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), detail]; }
        var activity = new AppActivity(Name(), state, detail);
        next.ActivitySequence++;
        next.ActivityRetention = [new RetainedSignal(next.ActivitySequence, activity with { Publisher = Id })];
        if (building) { await Save(next, new AppChanged(Name(), next.Version, next.Status), activity); }
        else { await Save(next, activity); }
    }

    public async Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null)
    {
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
            { if (!next.Settings.ContainsKey(setting.Name)) { next.Settings[setting.Name] = setting.Default; } }
        }
        foreach (var (path, source) in files) { next.Files[path] = source; }
        next.Edited = true;
        next.Version = Bump(next.Version, minor: firstBuild);
        next.Status = AppStatus.Live;
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "build finished"];
        Record(next);
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
        RequireCreated();
        await RequireCompiles();
        if (!CallerContextStamper.TryGet(out var caller))
        { throw new InvalidOperationException("Publishing needs a signed-in publisher."); }
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != BrainId())
        { throw new UnauthorizedAccessException("Only the brain that owns an app can publish it."); }
        // A listing is one app: the same name from another of the publisher's brains is refused,
        // never a silent takeover of what installers already trust.
        if (await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Read(caller.PrincipalId, Name()) is { } listed
            && listed.Source.Scope() != BrainId())
        { throw new InvalidOperationException($"{caller.PrincipalId}/{Name()} is already published from another brain."); }
        var next = Snapshot.Clone();
        var manifest = next.Manifest!;
        next.PublishedVersion = next.Version;
        next.Published = new AppVersionRecord(next.Version, clock.GetUtcNow(), manifest, next.Spec, new(next.Files, StringComparer.Ordinal));
        next.Publisher = caller.PrincipalId;
        var releases = Releases();
        var release = Release(next.Published, caller.PrincipalId, new AppSource.Brain(caller.AccountId, caller.BrainId));
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
        await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Upsert(new MarketplaceListing(
            caller.PrincipalId, new AppSource.Brain(caller.AccountId, caller.BrainId), name, manifest.Title, manifest.Description,
            next.Version, manifest.Icon));
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
                var held = new AppBundle(Name(), Snapshot.Version, Snapshot.Manifest, Snapshot.Spec, Snapshot.Files);
                if (held.Version != installation.Publication.Version || AppPublicationDigest.Of(held) != installation.Publication.Digest)
                { throw new InvalidOperationException("This installed copy has changed; it cannot resume the original install."); }
                return await Land(Snapshot.Clone());
            }
        }
        if (Snapshot.Manifest is not null)
        { throw new InvalidOperationException($"You already have an app named {Name()}."); }
        var next = Snapshot.Clone();
        next.BrainScopeId = BrainId();
        next.Principal = CallerContextStamper.TryGet(out var caller) ? caller : null;
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = new(bundle.Files, StringComparer.Ordinal);
        next.Settings = bundle.Manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        next.Connections = [];
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
            var attached = Snapshot.Clone();
            Adopt(attached, origin);
            await Persist(attached);
            await RecordFeature();
            return Describe(Snapshot);
        }
        var next = Snapshot.Clone();
        next.Manifest = bundle.Manifest.Validated();
        next.Spec = bundle.Spec;
        next.Files = new(bundle.Files, StringComparer.Ordinal);
        foreach (var setting in bundle.Manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.Settings[setting.Name] = setting.Default; } }
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
        next.Status = AppStatus.Deploying;
        Record(next);
        await Persist(next);
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
        RequireCreated();
        var next = Snapshot.Clone();
        next.Origin = origin;
        next.Edited = false;
        await Persist(next);
        await RecordFeature();
    }


    public async Task<AppSnapshot> SaveToSource()
    {
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
        if (Snapshot.Manifest is null) { return; }
        var key = this.GetPrimaryKeyString();
        // Only this app's own listing comes down with it.
        if (Snapshot.Publisher is { } publisher
            && await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Read(publisher, Name()) is { } listed
            && listed.Source.Scope() == BrainId())
        { await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Remove(publisher, Name()); }
        // Everything the app owns goes with it: its program, then its storage.
        if (Runtime is { } runtime) { await runtime.Retire(key, CancellationToken.None); }
        if (ServiceProvider.GetService<IKernel>() is { } kernel) { await kernel.UnloadFeature(BrainId(), Name()); }
        foreach (var hook in storageHooks) { await hook.OnRetired(Snapshot.BrainScopeId ?? "", key, CancellationToken.None); }
        // Clearing the record ends the app's existence: the kernel retires the instance.
        var gone = new AppState();
        await Clear(new AppChanged(Name(), gone.Version, gone.Status));
    }

    private async Task Reopen()
    {
        foreach (var hook in storageHooks) { await hook.OnCreated(BrainId(), this.GetPrimaryKeyString(), CancellationToken.None); }
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
        var requested = new AppRequested(operation, input, id) with { Publisher = Id };
        if (await Rejection(requested) is { } rejected)
        {
            await PublishAsync(new AppRejected(operation, rejected));
            await PublishAsync(new AppActivity(Name(), "attention", $"{rejected.Rule} refused {operation}: {rejected.Reason}"));
            throw new ArgumentException($"{rejected.Rule} refused {operation}: {rejected.Reason}");
        }
        // A request is a fact for the app's synapses; it is published for whoever is watching and kept
        // for whoever subscribes later, so a synapse that starts after the request still gets it.
        var next = Snapshot.Clone();
        var retained = new RetainedSignal(next.RequestSequence + 1, requested);
        next.RequestSequence = retained.Sequence;
        next.RequestRetention = [.. next.RequestRetention.TakeLast(RetainedCapacity - 1), retained];
        await Save(next, new AppRequested(operation, input, id));
        return id;
    }

    public async Task<AppSnapshot> WriteSynapse(string sentence, string[] focus, Guid operationId)
    {
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
        ServiceProvider.GetRequiredService<SynapseWriter>().Enqueue(this.GetPrimaryKeyString(), operationId, sentence.Trim(), keys);
        return Describe(Snapshot);
    }

    public async Task<StagedProgram> Stage(Guid operationId, string path, string source)
    {
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (path == AppFiles.DeclarationPath) { throw new ArgumentException($"{AppFiles.DeclarationPath} is the app's declaration: change it through SaveManifest and SaveSpec."); }
        if (!AppFiles.IsProgram(path)) { throw new ArgumentException("A program file is a .cs file: a synapse (top-level statements) or a shared class."); }
        var index = Array.FindIndex(Snapshot.Synapses, synapse => synapse.OperationId == operationId);
        if (index < 0 || Snapshot.Synapses[index].Status != SynapseStatus.Requested)
        { throw new InvalidOperationException("No requested synapse waits under that operation id."); }
        var sources = Sources(Snapshot);
        sources[path] = source;
        RequireShape(sources);
        var entry = Snapshot.Synapses[index];
        if (entry.Path != path || entry.Source != source)
        {
            var next = Snapshot.Clone();
            next.Synapses[index] = entry with { Path = path, Source = source };
            await Persist(next);
        }
        return new(sources, Configuration(Snapshot), Snapshot.Principal);
    }

    // A deleted app has no files, so a writer that outlived it deploys an empty program, which retires the runtime.
    public Task<StagedProgram> Program() => Task.FromResult(new StagedProgram(Sources(Snapshot), Configuration(Snapshot), Snapshot.Principal));

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
            next.Files[entry.Path!] = staged;
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
        Signal outcome = refused.Length == 0
            ? new SynapseWritten(entry.Path!, hash ?? entry.Hash ?? "", elapsedMs, attempts)
            : new SynapseRejected(entry.Sentence, refused);
        await Save(next, new AppChanged(Name(), next.Version, next.Status), outcome);
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

    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal switch
        {
            nameof(AppRequested) => [.. Snapshot.RequestRetention.Where(candidate => candidate.Sequence > since)],
            nameof(AppActivity) => [.. Snapshot.ActivityRetention.Where(candidate => candidate.Sequence > since)],
            _ => [],
        });

    private IAppRuntime? Runtime => runtimes.FirstOrDefault();

    private static Dictionary<string, string> Sources(AppState state)
        => state.Files.Where(file => AppFiles.IsProgram(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);

    // The whole program, every time: the runtime decides what changed. No program means nothing runs.
    private async Task Deploy()
    {
        await RecordFeature();
        if (Runtime is not { } runtime) { return; }
        var sources = Sources(Snapshot);
        if (sources.Count == 0) { await runtime.Retire(this.GetPrimaryKeyString(), CancellationToken.None); return; }
        RequireShape(sources);
        await runtime.Deploy(this.GetPrimaryKeyString(), sources, Configuration(Snapshot), CancellationToken.None);
    }

    // The shape gate in front of the runtime's compile gate: every synapse is one reaction.
    private static void RequireShape(IReadOnlyDictionary<string, string> sources)
    {
        if (SynapseShape.Check(sources) is { Length: > 0 } errors)
        { throw new InvalidOperationException("The program is not shaped as synapses: " + SynapseShape.Describe(errors)); }
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
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App"] = this.GetPrimaryKeyString(),
            ["Brain"] = state.BrainScopeId ?? "",
        };
        foreach (var (name, value) in state.Settings) { values[name] = value; }
        foreach (var (slot, id) in state.Connections) { values["Account__" + slot] = id; }
        return values;
    }

    private void Record(AppState next)
    {
        var record = new AppVersionRecord(next.Version, clock.GetUtcNow(), next.Manifest!, next.Spec,
            new(next.Files, StringComparer.Ordinal));
        // One entry per version: a version saved again replaces its entry.
        next.History = [.. next.History.Where(held => held.Version != next.Version).TakeLast(MaxHistory - 1), record];
    }

    private bool Replayed(Guid operationId) => Snapshot.Receipts.Any(receipt => receipt.OperationId == operationId);

    private static void Receipt(AppState next, Guid operationId)
    {
        next.Receipts.Add(new(operationId, "", ""));
        while (next.Receipts.Count > MaxReceipts) { next.Receipts.RemoveAt(0); }
    }

    private void RequireCreated()
    {
        if (Snapshot.Manifest is null)
        { throw new InvalidOperationException($"No app named {Name()} exists in this brain."); }
    }

    // The publish gate: every script in the bundle compiles against the installed contracts. A host
    // without a script compiler cannot run apps either, so it has nothing to refuse.
    private Task RequireCompiles() => RequireCompiles(Snapshot);

    private async Task RequireCompiles(AppState state)
    {
        var scripts = state.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        if (scripts.Count == 0) { return; }
        // The shape is syntax, so it is checked even where no compiler is.
        if (SynapseShape.Check(scripts) is { Length: > 0 } shape)
        { throw new InvalidOperationException($"Version {Snapshot.Version} is not shaped as synapses: " + SynapseShape.Describe(shape)); }
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
        var key = this.GetPrimaryKeyString();
        var marker = key.LastIndexOf("/apps/", StringComparison.Ordinal);
        return marker >= 0 ? key[(marker + "/apps/".Length)..] : key;
    }

    // The key is {brain}/apps/{name}; the brain scope is ambient in the address.
    private string BrainId()
    {
        var key = this.GetPrimaryKeyString();
        var marker = key.LastIndexOf("/apps/", StringComparison.Ordinal);
        return marker >= 0 ? key[..marker] : "";
    }

    private static string Bump(string version, bool minor)
    {
        var parts = version.Split('.');
        return minor ? $"{parts[0]}.{int.Parse(parts[1]) + 1}.0"
                     : $"{parts[0]}.{parts[1]}.{int.Parse(parts[2]) + 1}";
    }

    private Task Persist(AppState next)
        => Save(next, new AppChanged(Name(), next.Version, next.Status));

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
        state.Installation);
}

internal sealed class AppActivation : IActivationHandler<AppNeuron>
{
    public Task Activated(AppNeuron neuron, CancellationToken cancellationToken) => neuron.Interrupt();
}
