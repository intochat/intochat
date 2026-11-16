using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Units;
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
[DigitalBrain.Neurons.Neuron(IntoChat.OS.Connections.AppGrainType.Name, State = "app2")]
internal sealed partial class AppNeuron(
    TimeProvider clock, IOptions<AppsOptions> options,
    IEnumerable<IAppStorageHook> storageHooks,
    ILogger<AppNeuron>? logger = null,
    IMarketplace? marketplace = null,
    IHostedAppTypes? hostedAppTypes = null,
    IServiceProvider? services = null)
    : DigitalBrain.Neurons.Neuron<AppState>, IApp, IAppRestore
{
    private IServiceProvider? ServiceProvider => services;
    private static readonly AppState Cleared = new();
    // Whether the app has a record: created apps always carry a manifest.
    private bool Exists => State.Manifest is not null;
    // The os full key "{brain}/apps/{name}" the app was addressed by as a grain.
    private string Key => Id.FullKey();

    // Every activation reconciles an interrupted turn, then queues the program's restore as its own turn.
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Interrupt();
        if (State.Manifest is null || State.Refused is not null) { return; }
        if (await RefuseLegacy()) { return; }
        if (HasProgram(State)) { Defer(() => Get<IAppRestore>(Id).Restore()); }
    }

    // A record stored before 0.18 is read from its raw JSON (the typed state has already dropped the retired fields) and refused:
    // Failed, one activity line, nothing deployed. Whatever goes wrong here is logged, never thrown at the first caller.
    private async Task<bool> RefuseLegacy()
    {
        try
        {
            var markers = LegacyAppRecord.DetectMarkers(State);
            if (markers.Length == 0) { return false; }
            var next = State.Clone();
            LegacyAppRecord.Cleanse(next);
            // A shipped app the host loads (ino, desktop, creator...) is not rebuilt by the Creator: its program is the host's compiled
            // type, so the pre-0.18 leftovers are stripped and the record stays usable.
            var hosted = hostedAppTypes ?? services?.GetService<IHostedAppTypes>();
            var log = logger ?? services?.GetService<ILogger<AppNeuron>>();
            if (hosted is not null && hosted.IsHostLoaded(Name()))
            {
                log?.LogInformation(
                    "App {App} carried pre-0.18 leftovers ({Markers}); stripped, the host-loaded program stands.", Key, string.Join(", ", markers));
                await Persist(next);
                return false;
            }
            log?.LogWarning(
                "App {App} is a pre-0.18 record ({Markers}); it is refused and must be rebuilt.", Key, string.Join(", ", markers));
            // The refusal is durable: the program files and settings are stripped (nothing deploys or retries) and Refused names why, so
            // the activity line below is written once, not once per activation. A rebuild (Import or CompleteBuild) clears Refused.
            next.Refused = string.Join(", ", markers);
            next.Status = AppStatus.Failed;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), LegacyAppRecord.Refusal];
            await Persist(next);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            (logger ?? services?.GetService<ILogger<AppNeuron>>())?.LogError(error, "Could not check app {App} for a pre-0.18 record.", Key);
            return false;
        }
    }

    private Task Save(AppState next, DigitalBrain.Signal first, DigitalBrain.Signal second, DigitalBrain.Signal third) => base.Save(next, [first, second, third]);

    // Deleting the record ends the app's existence (the kernel retires the instance), then says so.
    private async Task Clear(DigitalBrain.Signal changed)
    {
        await Store(Cleared);
        _ = Cleared;
        await Publish(changed);
    }

    // The platform-wide Marketplace is still a grain outside every brain.
    private IMarketplace Marketplace => marketplace ?? (services is not null ? SiloNeurons.Get<IMarketplace>(services, IMarketplace.Key) : throw new InvalidOperationException("No services"));
    private const int MaxHistory = 3;
    private const int MaxActivity = 50;
    private const int MaxPayloadLength = 64 * 1024;
    private const int MaxReceipts = 64;
    private const int RetainedCapacity = 64;
    internal const int MaxFocus = 16;
    internal const int MaxFocusKeyLength = 512;

    private EdgeAuthority? Edge => services?.GetService<EdgeAuthority>();

    private CapabilityCaller CapturePrincipal()
    {
        var caller = Context.Caller;
        var brain = BrainId();
        if (caller is null) { return CapabilityCallers.Of(brain, brain, brain); }
        return caller.Actor switch
        {
            Actor.Person person => CapabilityCallers.Of(person.Principal, person.Account, brain, CallerKind.User, conversationId: caller.Conversation),
            Actor.Agent agent => CapabilityCallers.Of(agent.For.Principal, agent.For.Account, brain, CallerKind.Agent, conversationId: caller.Conversation),
            Actor.App app => CapabilityCallers.Of(app.For.Principal, app.For.Account, brain, CallerKind.App, app.Key, caller.Conversation),
            Actor.Scheduler => CapabilityCallers.Of(brain, brain, brain, CallerKind.Scheduler, conversationId: caller.Conversation),
            _ => CapabilityCallers.Of(brain, brain, brain, conversationId: caller.Conversation),
        };
    }

    public Task<AppSnapshot> Read() => Task.FromResult(Describe(State));

    public async Task<AppSnapshot> Create(AppManifest manifest, string spec)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await Check("Create", new AppNameIsFree(Name(), State.Manifest));
        var validated = manifest.Validated();
        var next = State.Clone();
        next.Manifest = validated;
        next.BrainScopeId = BrainId();
        next.Principal = CapturePrincipal();
        next.Spec = spec ?? "";
        next.Settings = validated.Settings.ToDictionary(setting => setting.Name, setting => setting.Default ?? string.Empty, StringComparer.Ordinal);
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
        await RequireCreated("SaveSpec");
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
        await RequireCreated("SaveManifest");
        ArgumentNullException.ThrowIfNull(manifest);
        if (Replayed(operationId)) { return Describe(State); }
        var validated = manifest.Validated();
        var next = State.Clone();
        next.Edited = true;
        next.Manifest = validated;
        // Newly declared settings get their defaults; chosen values survive.
        foreach (var setting in validated.Settings)
        { if (!next.Settings.ContainsKey(setting.Name)) { next.PutSetting(setting.Name, setting.Default ?? string.Empty); } }
        next.Version = Bump(next.Version, minor: false);
        Record(next);
        Receipt(next, operationId);
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections)
    {
        await Check("Configure",
            new AppExists(Name(), State.Manifest),
            new AppRecordIsUsable(Name(), State.Refused),
            new SettingsAreNotReserved(settings?.Keys ?? (IReadOnlyCollection<string>)[]));
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
            new AppExists(Name(), State.Manifest),
            new AppRecordIsUsable(Name(), State.Refused),
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
        await Check("BeginBuild",
            new AppExists(Name(), State.Manifest),
            new BuilderIsIdle(State.Status, State.BuildStartedAt, TimeSpan.FromSeconds(options.Value.BuildFreshForSeconds), clock.GetUtcNow()));
        var next = State.Clone();
        next.Status = AppStatus.Building;
        next.BuildAttempt = Guid.NewGuid();
        next.BuildStartedAt = clock.GetUtcNow();
        // The build flow's id is known before the flow starts, so its FlowDone, however early, is this attempt's.
        next.BuildFlowId = new DigitalBrain.NeuronId(Id.Brain, Flows.BuildFlow.Alias, BuildFlowKey(next.BuildAttempt)).ToString();
        await Context.Synapses.EnsureBrainWide(typeof(DigitalBrain.Flows.FlowDone));
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

    // The key of an attempt's build flow: the app's name and the attempt, unique per attempt and free of '/'.
    internal string BuildFlowKey(Guid attempt) => $"{Name()}-{attempt:N}";


    public Task<string?> BuildFlow() => Task.FromResult(State.BuildFlowId);

    public async Task Progress(string state, string detail)
    {
        // A background writer may report after deletion; activity must not recreate its app record.
        if (State.Manifest is null) { return; }
        await Check("Progress", new ActivityStateIsKnown(state ?? ""));
        detail = (detail ?? "").Trim();
        var next = State.Clone();
        var building = next.Status == AppStatus.Building && detail.Length > 0
            && next.Activity.LastOrDefault() != detail;
        if (building) { next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), detail]; }
        var activity = new AppActivity(Name(), state!, detail);
        next.ActivitySequence++;
        next.LastActivity = activity;
        var presentation = PresentationCollaborator.ApplyActivity(next, Key, state!);
        await Store(next);
        await PublishEphemeral(activity);
        if (presentation is not null)
        { await PublishEphemeral(presentation); }
    }

    public async Task<AppSnapshot> CompleteUnitBuild(Guid attempt, string hash, DigitalBrain.Units.InstallStatus status, string sentence)
    {
        if (State.Status != AppStatus.Building || attempt != State.BuildAttempt) { return Describe(State); }
        var next = State.Clone();
        if (status is DigitalBrain.Units.InstallStatus.Installed or DigitalBrain.Units.InstallStatus.Updated or DigitalBrain.Units.InstallStatus.Unchanged)
        {
            next.Status = AppStatus.Live;
            // The unit build is how the Creator rebuilds a refused record: it lifts the refusal.
            next.Refused = null;
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), $"build finished: synapse unit {hash}"];
        }
        else
        {
            // Pending: the unit is deployed but its needs wait for the person; the app stays Building until they are installed.
            var line = $"waiting for approval: {sentence}";
            if (next.Activity.LastOrDefault() == line) { return Describe(State); }
            next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), line];
        }
        await Persist(next);
        return Describe(State);
    }

    public async Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null)
    {
        if (State.Status != AppStatus.Building || attempt != State.BuildAttempt) { return Describe(State); }
        var next = State.Clone();
        next.Status = AppStatus.Failed;
        next.Activity = [.. next.Activity.TakeLast(MaxActivity - 1), "build failed: " + (error ?? "the legacy file builder is retired; units use CompleteUnitBuild")];
        await Persist(next);
        return Describe(State);
    }

    public async Task Delete()
    {
        if (State.Manifest is null) { return; }
        var key = Key;
        // Only this app's own listing comes down with it.
        if (State.Publisher is { } publisher)
        {
            using (Edge?.Enter(Edge.Module("apps.catalog", new BrainId("system"))))
            {
                if (await Marketplace.Read(publisher, Name()) is { } listed
                    && listed.Source.Scope() == BrainId())
                { await Marketplace.Remove(publisher, Name()); }
            }
        }
        // Everything the app owns goes with it: its program, then its storage.
        if (ServiceProvider?.GetService<ISandboxRuntimeBridge>() is { } bridge)
        {
            await bridge.UnloadAsync(BrainId(), Name(), CancellationToken.None).ConfigureAwait(false);
        }
        if (ServiceProvider?.GetService<IKernel>() is { } kernel) { await kernel.UnloadFeature(BrainId(), Name()); }
        foreach (var hook in storageHooks) { await hook.OnRetired(State.BrainScopeId ?? "", key, CancellationToken.None); }
        await RemoveAppPrincipal();
        // Clearing the record ends the app's existence: the kernel retires the instance.
        await Clear(new AppChanged(Name(), Cleared.Version, Cleared.Status));
    }

    private async Task Reopen()
    {
        foreach (var hook in storageHooks) { await hook.OnCreated(BrainId(), Key, CancellationToken.None); }
    }

    // A view's emit is a request by another name: the same rules, judge, retention and AppRequested.
    public Task<Guid> Emit(string signal, string args) => Request(signal, args);

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
        var requested = new AppRequested(operation, input, id);
        // A request is a fact for the app's synapses; it is published for whoever is watching and kept
        // for whoever subscribes later, so a synapse that starts after the request still gets it.
        var next = State.Clone();
        var retained = new RetainedRequest(next.RequestSequence + 1, requested);
        next.RequestSequence = retained.Sequence;
        next.RequestRetention = [.. next.RequestRetention.TakeLast(RetainedCapacity - 1), retained];
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
        => Check(operation, new AppExists(Name(), State.Manifest));

    private string Name()
        => IntoChat.OS.Primitives.AppRef.TryParse(Id.Brain, Key, out var app)
            ? app.Name
            : IntoChat.OS.Primitives.AppRef.TryParse(Id.Brain, Id.Key, out var alt)
                ? alt.Name
                : Id.Key;

    // The key is {brain}/apps/{name}; the brain scope is ambient in the address.
    private string BrainId() => Id.Brain.Value;

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
        state.Installation,
        state.Presentation,
        state.ProgramRevision);
}
