using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

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
    private string Key => Id.FullKey();

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
        next.Principal = RuntimeCallerContext.TryGet(out var caller) ? caller : await HostCaller.ForBrain(ServiceProvider, BrainId());
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
        var presentation = PresentationCollaborator.ApplyActivity(next, Key, "attention");
        return presentation is null ? Save(next, interrupted) : Save(next, interrupted, presentation);
    }

    public Task<Guid> CurrentBuildAttempt() => Task.FromResult(State.BuildAttempt);

    public async Task BuildFlowStarted(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        if (State.Manifest is null || State.BuildFlowId == flowId) { return; }
        await Context.Synapses.EnsureBrainWide(typeof(DigitalBrain.Flows.FlowDone));
        var next = State.Clone();
        next.BuildFlowId = flowId;
        await Persist(next);
    }

    public Task<string?> BuildFlow() => Task.FromResult(State.BuildFlowId);

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
        var presentation = PresentationCollaborator.ApplyActivity(State, Key, state!, blackboard);
        await PublishEphemeral(activity);
        if (presentation is not null)
        { await PublishEphemeral(presentation); }
    }

    public async Task<AppSnapshot> CompleteHeadlessBuild(Guid attempt, string hash)
    {
        if (State.MigrationPaused || State.Status != AppStatus.Building || attempt != State.BuildAttempt) { return Describe(State); }
        var next = State.Clone();
        next.Status = AppStatus.Live;
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), $"build finished: synapse unit {hash}"];
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> CompleteBuild(Guid attempt,Dictionary<string, string>? files, string? error, AppManifest? manifest = null)
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
    {
        return next.ProgramRevision == State.ProgramRevision
            ? Save(next, new AppChanged(Name(), next.Version, next.Status))
            : Save(next, new AppChanged(Name(), next.Version, next.Status),
                new PresentationChanged(Key, next.ProgramRevision, 0));
    }

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
