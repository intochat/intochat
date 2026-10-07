using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Apps;

// The app: the only durable thing in the module. Manifest, spec, files, connections,
// semver history, status and activity all live here; builders and verifiers are caches
// that write their results back in.
[GrainType(DigitalBrain.Framework.AppGrainType.Name)]
internal sealed class AppNeuron(
    [PersistentState("app2", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<AppState> store,
    TimeProvider clock, IOptions<AppsOptions> options,
    IEnumerable<IDeployableRuntime> deployables, IEnumerable<IAppStorageHook> storageHooks)
    : Neuron<AppState>(store), IApp
{
    private const int MaxHistory = 20;
    private const int MaxActivity = 50;
    private const int MaxInvocations = 256;
    private const int MaxPayloadLength = 64 * 1024;
    private const int MaxReceipts = 64;

    // A published version is public by definition: anyone may copy it, nothing more.
    public override NeuronAccess Access(string operation)
        => operation == nameof(IApp.ExportPublished) ? NeuronAccess.PublicOperation : base.Access(operation);

    public Task<AppSnapshot> Read() => Task.FromResult(Describe(Snapshot));

    public async Task<AppSnapshot> Create(AppManifest manifest, string spec)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Snapshot.Manifest is not null)
        { throw new InvalidOperationException($"You already have an app named {Name()}. Open it in the Creator, or describe this one differently."); }
        var next = Snapshot.Clone();
        next.BrainScopeId = BrainId();
        next.Manifest = manifest;
        next.Spec = spec ?? "";
        next.Settings = manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        next.Version = "0.1.0";
        next.Status = AppStatus.Drafted;
        Record(next);
        await TrackUnlessScratch();
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
        next.Manifest = manifest;
        // Newly declared settings get their defaults; chosen values survive.
        foreach (var setting in manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.Settings[setting.Name] = setting.Default; } }
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId)
    {
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!AppFiles.IsSynapse(path)) { throw new ArgumentException("A synapse is a synapses/*.cs file."); }
        if (Replayed(operationId)) { return Describe(Snapshot); }
        var next = Snapshot.Clone();
        next.Edited = true;
        var swap = next.Files.ContainsKey(path);
        next.Files[path] = source;
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        // Keys are tracked before anything deploys: a failure part-way can never leave a
        // running file that delete and the storage hooks cannot see.
        var keys = Deployable()?.Keys(this.GetPrimaryKeyString(), next.ProgramGeneration, [path]) ?? [];
        next.StorageFiles = [.. next.StorageFiles.Concat(keys).Distinct(StringComparer.Ordinal)];
        await Persist(next);
        await DeployBehavior(path, source, swap);
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> WriteFile(string path, string source, Guid operationId)
    {
        RequireCreated();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (AppFiles.IsSynapse(path)) { throw new ArgumentException("Programs are written through WriteBehavior, which deploys them under synapses/."); }
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
        var next = Snapshot.Clone();
        foreach (var (name, value) in settings ?? []) { next.Settings[name] = value; }
        foreach (var (slot, id) in connections ?? []) { next.Connections[slot] = id; }
        await Persist(next);
        // Behaviors read their settings from the deployment configuration; a configure
        // re-swaps them so the running scripts see the new values.
        await RedeployBehaviors();
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Rollback(string version)
    {
        RequireCreated();
        var record = Snapshot.History.FirstOrDefault(item => item.Version == version)
            ?? throw new ArgumentException($"No version {version}. This app has: {string.Join(", ", Snapshot.History.Select(item => item.Version))}.");
        // Behaviors the target version lacks stop running: they are retired before the
        // surviving ones redeploy, and their tracked keys leave with them.
        var removed = Snapshot.Files.Keys.Where(AppFiles.IsSynapse)
            .Where(path => !record.Files.ContainsKey(path)).ToArray();
        var removedKeys = Deployable()?.Keys(this.GetPrimaryKeyString(), Snapshot.ProgramGeneration, removed) ?? [];
        if (Deployable() is { } runtime && removedKeys.Length > 0)
        {
            await runtime.Retire(this.GetPrimaryKeyString(), removedKeys, CancellationToken.None);
            foreach (var hook in storageHooks)
            { await hook.OnRetired(Snapshot.BrainScopeId ?? "", this.GetPrimaryKeyString(), removedKeys, CancellationToken.None); }
        }
        var next = Snapshot.Clone();
        next.Edited = true;
        next.Manifest = record.Manifest;
        next.Spec = record.Spec;
        next.Files = new(record.Files, StringComparer.Ordinal);
        next.StorageFiles = [.. next.StorageFiles.Except(removedKeys, StringComparer.Ordinal)];
        next.DeployedFiles = [.. next.DeployedFiles.Except(removedKeys, StringComparer.Ordinal)];
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        await Persist(next);
        await RedeployBehaviors();
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

    public Task<Guid> CurrentBuildAttempt() => Task.FromResult(Snapshot.BuildAttempt);

    public async Task Progress(string line)
    {
        if (Snapshot.Status != AppStatus.Building || string.IsNullOrWhiteSpace(line)) { return; }
        var next = Snapshot.Clone();
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), line.Trim()];
        await Persist(next);
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
        if (files.ContainsKey(AppFiles.SpecPath))
        {
            next.Status = AppStatus.Failed;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), $"build failed: {AppFiles.SpecPath} belongs to the Author, not the Builder"];
            await Persist(next);
            return Describe(Snapshot);
        }
        var existingBehaviors = Snapshot.Files.Keys.Where(AppFiles.IsSynapse).ToHashSet(StringComparer.Ordinal);
        var firstTests = files.ContainsKey(AppFiles.TestsPath) && !next.Files.ContainsKey(AppFiles.TestsPath);
        if (manifest is not null)
        {
            next.Manifest = manifest;
            foreach (var setting in manifest.Settings)
            { if (!next.Settings.ContainsKey(setting.Name)) { next.Settings[setting.Name] = setting.Default; } }
        }
        foreach (var (path, source) in files) { next.Files[path] = source; }
        next.Edited = true;
        var behaviorPaths = files.Keys.Where(AppFiles.IsSynapse).ToArray();
        var keys = Deployable()?.Keys(this.GetPrimaryKeyString(), next.ProgramGeneration, behaviorPaths) ?? [];
        next.StorageFiles = [.. next.StorageFiles.Concat(keys).Distinct(StringComparer.Ordinal)];
        next.Version = Bump(next.Version, minor: firstTests);
        next.Status = AppStatus.Live;
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "build finished"];
        Record(next);
        await Persist(next);
        foreach (var path in behaviorPaths) { await DeployBehavior(path, files[path], swap: existingBehaviors.Contains(path)); }
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> BeginVerify()
    {
        RequireCreated();
        if (!Snapshot.Files.ContainsKey(AppFiles.TestsPath))
        { throw new InvalidOperationException($"This app has no {AppFiles.TestsPath} yet; implement it first."); }
        var next = Snapshot.Clone();
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "verification started"];
        await Persist(next);
        return Describe(Snapshot);
    }

    public async Task RecordVerdicts(AppScenarioVerdict[] verdicts, bool green, string? version = null)
    {
        RequireCreated();
        ArgumentNullException.ThrowIfNull(verdicts);
        var tested = version ?? Snapshot.Version;
        var next = Snapshot.Clone();
        next.Verified[tested] = green;
        // Verdicts for a version the app has moved past stay informative, never current facts.
        if (tested == next.Version) { next.Verdicts = verdicts; }
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1),
            (green ? "verification green" : "verification red") + (tested == next.Version ? "" : $" (for {tested})")];
        var name = Name();
        await Save(next, new AppChanged(name, next.Version, next.Status), new AppVerified(name, tested, green));
    }

    public Task<AppBundle> Export()
    {
        RequireCreated();
        if (!Snapshot.Verified.TryGetValue(Snapshot.Version, out var green) || !green)
        { throw new InvalidOperationException($"Version {Snapshot.Version} has not passed its tests; only green versions can be shared."); }
        var manifest = Snapshot.Manifest!;
        return Task.FromResult(new AppBundle(Name(), Snapshot.Version, manifest, Snapshot.Spec,
            new(Snapshot.Files, StringComparer.Ordinal)));
    }

    public async Task<AppSnapshot> Publish()
    {
        RequireCreated();
        if (!Snapshot.Verified.TryGetValue(Snapshot.Version, out var green) || !green)
        { throw new InvalidOperationException($"Version {Snapshot.Version} has not passed its tests; only green versions can be published."); }
        if (!CallerContextStamper.TryGet(out var caller))
        { throw new InvalidOperationException("Publishing needs a signed-in publisher."); }
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != BrainId())
        { throw new UnauthorizedAccessException("Only the brain that owns an app can publish it."); }
        // A listing is one app: the same name from another of the publisher's brains is refused,
        // never a silent takeover of what installers already trust.
        if (await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Read(caller.PrincipalId, Name()) is { } listed
            && BrainScope.Create(listed.PublisherAccountId, listed.PublisherBrainId).Id != BrainId())
        { throw new InvalidOperationException($"{caller.PrincipalId}/{Name()} is already published from another brain."); }
        var next = Snapshot.Clone();
        var manifest = next.Manifest!;
        next.PublishedVersion = next.Version;
        next.Published = new AppVersionRecord(next.Version, clock.GetUtcNow(), manifest, next.Spec, new(next.Files, StringComparer.Ordinal));
        next.Publisher = caller.PrincipalId;
        var name = Name();
        await Save(next, new AppChanged(name, next.Version, next.Status), new AppPublished(name, next.Version));
        // State first: a failed listing write is repaired by publishing again, never the reverse
        // (a listing whose bundle Install cannot read).
        await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Upsert(new MarketplaceListing(
            caller.PrincipalId, caller.AccountId, caller.BrainId, name, manifest.Title, manifest.Description,
            next.Version, manifest.Icon, AppVerifyJob.ScenarioNames(next.Spec)));
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

    public async Task<AppSnapshot> Import(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        RequireBundleFor(bundle);
        if (Snapshot.Manifest is not null)
        { throw new InvalidOperationException($"You already have an app named {Name()}."); }
        var next = Snapshot.Clone();
        next.BrainScopeId = BrainId();
        next.Manifest = bundle.Manifest;
        next.Spec = bundle.Spec;
        next.Files = new(bundle.Files, StringComparer.Ordinal);
        next.Settings = bundle.Manifest.Settings.ToDictionary(setting => setting.Name, setting => setting.Default, StringComparer.Ordinal);
        next.Connections = [];
        next.Version = bundle.Version;
        next.Status = AppStatus.Live;
        var behaviors = next.Files.Keys.Where(AppFiles.IsSynapse).ToArray();
        next.StorageFiles = Deployable()?.Keys(this.GetPrimaryKeyString(), next.ProgramGeneration, behaviors) ?? [];
        Record(next);
        await TrackUnlessScratch();
        await Persist(next);
        foreach (var path in behaviors) { await DeployBehavior(path, next.Files[path], swap: false); }
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Replace(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        RequireCreated();
        RequireBundleFor(bundle);
        var appKey = this.GetPrimaryKeyString();
        var existing = Snapshot.Files.Keys.Where(AppFiles.IsSynapse).ToHashSet(StringComparer.Ordinal);
        var removed = existing.Where(path => !bundle.Files.ContainsKey(path)).ToArray();
        var removedKeys = Deployable()?.Keys(appKey, Snapshot.ProgramGeneration, removed) ?? [];
        // Only the removed behaviors' files stop. The storage hooks are not told: their OnRetired
        // retires everything the app owns, and an update keeps the app's data.
        if (Deployable() is { } runtime && removedKeys.Length > 0)
        { await runtime.Retire(appKey, removedKeys, CancellationToken.None); }
        var next = Snapshot.Clone();
        next.Manifest = bundle.Manifest;
        next.Spec = bundle.Spec;
        next.Files = new(bundle.Files, StringComparer.Ordinal);
        foreach (var setting in bundle.Manifest.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.Settings[setting.Name] = setting.Default; } }
        var behaviors = next.Files.Keys.Where(AppFiles.IsSynapse).ToArray();
        var keys = Deployable()?.Keys(appKey, next.ProgramGeneration, behaviors) ?? [];
        next.StorageFiles = [.. next.StorageFiles.Except(removedKeys, StringComparer.Ordinal).Concat(keys).Distinct(StringComparer.Ordinal)];
        next.DeployedFiles = [.. next.DeployedFiles.Except(removedKeys, StringComparer.Ordinal)];
        foreach (var path in removed) { next.BehaviorErrors.Remove(path); }
        next.Version = bundle.Version;
        next.Status = AppStatus.Live;
        Record(next);
        await Persist(next);
        foreach (var path in behaviors) { await DeployBehavior(path, next.Files[path], swap: existing.Contains(path)); }
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
    }


    public async Task Delete()
    {
        if (Snapshot.Manifest is null) { return; }
        // Only this app's own listing comes down with it.
        if (Snapshot.Publisher is { } publisher
            && await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Read(publisher, Name()) is { } listed
            && BrainScope.Create(listed.PublisherAccountId, listed.PublisherBrainId).Id == BrainId())
        { await GrainFactory.GetGrain<IMarketplace>(IMarketplace.Key).Remove(publisher, Name()); }
        if (Deployable() is { } deployable && Snapshot.StorageFiles.Length > 0)
        {
            await deployable.Retire(this.GetPrimaryKeyString(), Snapshot.StorageFiles, CancellationToken.None);
            foreach (var hook in storageHooks)
            { await hook.OnRetired(Snapshot.BrainScopeId ?? "", this.GetPrimaryKeyString(), Snapshot.StorageFiles, CancellationToken.None); }
        }
        await GrainFactory.GetGrain<IAppIndex>(BrainId()).Untrack(Name());
        await Persist(new AppState { BrainScopeId = Snapshot.BrainScopeId });
    }

    public async Task<AppInvocation> Invoke(InvokeApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCreated();
        if (Snapshot.Invocations.Find(item => item.Id == request.InvocationId) is { } existing) { return existing; }
        if (!Snapshot.Manifest!.Operations.Any(operation => operation.Name == request.Operation))
        {
            throw new ArgumentException(
                $"{request.Operation} is not an operation of {Name()}. It offers: {string.Join(", ", Snapshot.Manifest.Operations.Select(operation => operation.Name))}.");
        }
        if (request.Input is not { Length: <= MaxPayloadLength }) { throw new ArgumentException("An invocation input is at most 64 KiB."); }
        var invocation = new AppInvocation(request.InvocationId, request.Operation, request.Input, InvocationStatus.Pending, null, null, clock.GetUtcNow(), null);
        var next = Snapshot.Clone();
        next.Invocations.Add(invocation);
        while (next.Invocations.Count > MaxInvocations)
        {
            var oldest = next.Invocations.FindIndex(item => item.Status != InvocationStatus.Pending);
            if (oldest < 0) { throw new InvalidOperationException($"{MaxInvocations} invocations are still waiting for the app's script."); }
            next.Invocations.RemoveAt(oldest);
        }
        await Save(next, new AppInvoked(invocation.Id, invocation.Operation, invocation.Input));
        return invocation;
    }

    public async Task<AppInvocation> Respond(AppResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var index = Snapshot.Invocations.FindIndex(item => item.Id == response.InvocationId);
        if (index < 0) { throw new KeyNotFoundException($"No invocation {response.InvocationId}."); }
        var invocation = Snapshot.Invocations[index];
        if (invocation.Status != InvocationStatus.Pending) { return invocation; }
        if (response.Output is { Length: > MaxPayloadLength } || response.Error is { Length: > MaxPayloadLength })
        { throw new ArgumentException("An invocation output is at most 64 KiB."); }
        var completed = invocation with
        {
            Status = response.Error is null ? InvocationStatus.Completed : InvocationStatus.Failed,
            Output = response.Output,
            Error = response.Error,
            CompletedAt = clock.GetUtcNow(),
        };
        var next = Snapshot.Clone();
        next.Invocations[index] = completed;
        await Save(next, new AppInvocationCompleted(completed));
        return completed;
    }

    public Task<AppInvocation> ReadInvocation(Guid invocationId)
        => Snapshot.Invocations.Find(item => item.Id == invocationId) is { } invocation
            ? Task.FromResult(invocation)
            : throw new KeyNotFoundException($"No invocation {invocationId}.");

    public Task<IReadOnlyList<AppInvocation>> Pending()
        => Task.FromResult<IReadOnlyList<AppInvocation>>(Snapshot.Invocations.Where(item => item.Status == InvocationStatus.Pending).ToArray());

    private IDeployableRuntime? Deployable() => deployables.FirstOrDefault(candidate => candidate.Name == AppFiles.Runtime);

    // A verification scratch copy is working state, not one of the brain's apps.
    private Task TrackUnlessScratch()
        => Name().Contains(AppFiles.VerifyMarker, StringComparison.Ordinal)
            ? Task.CompletedTask
            : GrainFactory.GetGrain<IAppIndex>(BrainId()).Track(Name());

    private async Task DeployBehavior(string path, string source, bool swap)
    {
        if (Deployable() is not { } deployable) { return; }
        var deployment = new RuntimeDeployment(this.GetPrimaryKeyString(), Snapshot.BrainScopeId ?? "",
            Snapshot.ProgramGeneration, new(StringComparer.Ordinal) { [path] = source }, Configuration());
        var errors = new Dictionary<string, string>(Snapshot.BehaviorErrors, StringComparer.Ordinal);
        try
        {
            var deployed = swap
                ? await deployable.Swap(deployment, CancellationToken.None)
                : await deployable.Deploy(deployment, CancellationToken.None);
            errors.Remove(path);
            var next = Snapshot.Clone();
            next.DeployedFiles = [.. next.DeployedFiles.Concat(deployed).Distinct(StringComparer.Ordinal)];
            next.BehaviorErrors = errors;
            await Persist(next);
        }
        // Red is a state to revisit, not an error to unwind: the app keeps serving.
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            errors[path] = failure.Message;
            var next = Snapshot.Clone();
            next.BehaviorErrors = errors;
            await Persist(next);
        }
    }

    private async Task RedeployBehaviors()
    {
        foreach (var path in Snapshot.Files.Keys.Where(AppFiles.IsSynapse).ToArray())
        { await DeployBehavior(path, Snapshot.Files[path], swap: true); }
    }

    // The script reads brain.Setting("App") to find this neuron, brain.Setting(name) for each
    // setting and brain.Setting("Account__" + slot) for each connected account.
    private Dictionary<string, string> Configuration()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App"] = this.GetPrimaryKeyString(),
            ["Brain"] = Snapshot.BrainScopeId ?? "",
        };
        foreach (var (name, value) in Snapshot.Settings) { values[name] = value; }
        foreach (var (slot, id) in Snapshot.Connections) { values["Account__" + slot] = id; }
        return values;
    }

    private void Record(AppState next)
    {
        var record = new AppVersionRecord(next.Version, clock.GetUtcNow(), next.Manifest!, next.Spec,
            new(next.Files, StringComparer.Ordinal));
        next.History = [.. next.History.TakeLast(MaxHistory - 1), record];
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
        [.. state.History.Select(record => new AppVersionSummary(record.Version, record.SavedAt,
            state.Verified.TryGetValue(record.Version, out var green) ? green : null))],
        [.. state.Verdicts],
        state.PublishedVersion,
        state.Origin,
        state.Edited);
}
