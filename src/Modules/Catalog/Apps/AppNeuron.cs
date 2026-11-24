using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Modules.Apps;

// The app: the only durable thing in the module. Manifest, spec, files, connections,
// semver history, status and activity all live here; the Builder is a cache that writes
// its results back in.
// A plain neuron (06-08) with the persisted AppState ("app2", clean break R4-24). Delete clears the record; until the next save
// the app then reads as never created (the DigitalBrain base keeps the last state in memory, so the cleared view is held here).
[DigitalBrain.Neurons.Neuron(IntoChat.OS.Host.Primitives.AppGrainType.Name, State = "app2")]
internal sealed partial class AppNeuron(
    TimeProvider clock, IOptions<AppsOptions> options,
    IEnumerable<IAppStorageHook> storageHooks,
    ILogger<AppNeuron>? logger = null,
    IMarketplace? marketplace = null,
    IHostedAppTypes? hostedAppTypes = null,
    IServiceProvider? services = null)
    : DigitalBrain.Neurons.Neuron<AppState>, IApp, IAppRestore
{
    [DigitalBrain.Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)]
    private partial IConfirmationRequests? Confirmations { get; }

    [DigitalBrain.Slot(Key = "main", Required = false)]
    private partial IConnectionRequests? ConnectionCatalog { get; }

    private IServiceProvider? ServiceProvider => services;
    private static readonly AppState Cleared = new();
    // Whether the app has a record: created apps always carry a manifest.
    private bool Exists => State.Manifest is not null;
    // The os full key "{brain}/apps/{name}" the app was addressed by as a grain.
    private string Key => Id.FullKey();
    // The component id this app's typed bindings are recorded under in IInstalls.
    private ComponentId AppComponentId => new("app." + Name());

    // Every activation reconciles an interrupted turn, then queues the program's restore as its own turn.
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await MigrateLegacyPrincipal();
        await Interrupt();
        if (Confirmations is not null)
        {
            await Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain));
        }
        if (State.Manifest is null || State.Refused is not null) { return; }
        if (await RefuseLegacy()) { return; }
        await MigrateLegacyConnections(cancellationToken);
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

    // The one-time v1 migration of the stored caller into the publisher source (owner decision 1, 17 Nov).
    private async Task MigrateLegacyPrincipal()
    {
        if (State.LegacyPrincipal is null) { return; }
        var next = State.Clone();
        if (next.MigrateLegacyPrincipal()) { await Store(next); }
    }

    // Mirrors legacy AppState.Connections into IInstalls typed bindings when IInstalls does not yet hold typed bindings for this app.
    private async Task MigrateLegacyConnections(CancellationToken cancellationToken)
    {
        if (State.ConnectionEntries.Length == 0) { return; }
        var bindings = TypedConnectionBindings(State);
        if (bindings.Length == 0) { return; }
        try
        {
            var installs = Get<IInstalls>(new DigitalBrain.NeuronId(Id.Brain, "synapse.installs", "default"));
            var existing = await installs.Describe(AppComponentId.Value, cancellationToken);
            if (existing?.Typed is { Length: > 0 }) { return; }
            await installs.BindComponent(AppComponentId, bindings, cancellationToken);
        }
        catch (InvalidOperationException error)
        {
            (logger ?? services?.GetService<ILogger<AppNeuron>>())?.LogDebug(
                error, "Could not migrate legacy connections for app {App} because installs neuron is unavailable.", Key);
        }
    }

    // Writes connection slot bindings into IInstalls alongside any existing non-connection typed bindings.
    private async Task SyncConnectionBindings(AppState state, CancellationToken cancellationToken)
    {
        var bindings = TypedConnectionBindings(state);
        if (bindings.Length == 0) { return; }
        try
        {
            var installs = Get<IInstalls>(new DigitalBrain.NeuronId(Id.Brain, "synapse.installs", "default"));
            var existing = await installs.Describe(AppComponentId.Value, cancellationToken);
            var merged = MergeTypedBindings(existing?.Typed, bindings);
            await installs.BindComponent(AppComponentId, merged, cancellationToken);
        }
        catch (InvalidOperationException error)
        {
            (logger ?? services?.GetService<ILogger<AppNeuron>>())?.LogDebug(
                error, "Could not sync typed bindings for app {App} because installs neuron is unavailable.", Key);
        }
    }

    private Binding[] TypedConnectionBindings(AppState state)
    {
        var connections = state.Connections;
        if (connections.Count == 0) { return []; }
        var brain = BrainId();
        var slots = state.Manifest?.Slots ?? [];
        var result = new List<Binding>(connections.Count);
        foreach (var (slot, connectionId) in connections)
        {
            if (string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(connectionId)) { continue; }
            string key;
            if (ConnectionKeys.Is(connectionId))
            {
                key = connectionId;
            }
            else
            {
                var declared = slots.FirstOrDefault(s => string.Equals(s.Name, slot, StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(declared?.Connector)) { continue; }
                key = ConnectionKeys.For(brain, declared.Connector, connectionId);
            }
            result.Add(new Binding(slot, new BindingTarget.Connection(key)));
        }
        return [.. result];
    }

    private static Binding[] MergeTypedBindings(Binding[]? existing, Binding[] updated)
    {
        if (existing is not { Length: > 0 }) { return updated; }
        var updatedSlots = new HashSet<string>(updated.Select(b => b.Slot), StringComparer.Ordinal);
        return [.. existing.Where(b => !updatedSlots.Contains(b.Slot)), .. updated];
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

    // The account and brain of the person the stamped caller acts for; none for the kernel, a module or the scheduler.
    private AppPublisher? CapturePublisher()
        => Context.Caller is { For: { } person } caller && CallerRules.ActsFor(caller) is not null
            ? new AppPublisher(person.Account, caller.Brain.Value)
            : null;

    private bool IsDirectPerson(Caller? caller)
        => caller is { Direct: true, Conversation: null, Actor: Actor.Person }
            && string.Equals(caller.Brain.Value, BrainId(), StringComparison.Ordinal);

    public Task<AppSnapshot> Read() => Task.FromResult(Describe(State));

    public async Task<AppSnapshot> Create(AppManifest manifest, string spec)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await Check("Create", new AppNameIsFree(Name(), State.Manifest));
        var validated = manifest.Validated();
        var next = State.Clone();
        next.Manifest = validated;
        next.BrainScopeId = BrainId();
        next.PublisherSource = CapturePublisher();
        next.Spec = spec ?? "";
        next.Settings = validated.Settings.ToDictionary(setting => setting.Name, setting => setting.Default ?? string.Empty, StringComparer.Ordinal);
        next.Version = "0.1.0";
        // Pure-data Rung 1 agent apps require no code build and transition directly to Live.
        next.Status = validated.Agent is not null && next.FileEntries.Length == 0 ? AppStatus.Live : AppStatus.Drafted;
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

        Dictionary<string, string>? canonicalConnections = null;
        if (connections is { Count: > 0 })
        {
            var changesConnections = connections.Any(pair =>
                !State.Connections.TryGetValue(pair.Key, out var existing)
                || !string.Equals(existing, pair.Value, StringComparison.Ordinal));
            if (changesConnections && !IsDirectPerson(Context.Caller))
            {
                throw new UnauthorizedAccessException("Only a person can change app connection bindings; use BindSlots to request approval.");
            }

            var (resolved, refusal) = await ResolveSlotBindingsAsync(State.Manifest!, connections, CancellationToken.None);
            if (refusal is not null)
            {
                throw new InvalidOperationException(refusal);
            }
            canonicalConnections = resolved;

            // A storage hook vetoes a binding change its data depends on, before anything changes.
            foreach (var (slot, id) in canonicalConnections!)
            {
                if (State.Connections.TryGetValue(slot, out var current) && current == id) { continue; }
                foreach (var hook in storageHooks)
                { await hook.RequireRebindable(State.BrainScopeId ?? "", Key, slot, CancellationToken.None); }
            }
        }

        var next = State.Clone();
        foreach (var (name, value) in settings ?? []) { next.PutSetting(name, value); }
        foreach (var (slot, id) in canonicalConnections ?? []) { next.PutConnection(slot, id); }
        AdvanceProgram(next);
        await Persist(next);
        if (canonicalConnections is { Count: > 0 })
        {
            await SyncConnectionBindings(next, CancellationToken.None);
        }
        // The running program reads its settings from the deployment configuration, so a
        // configure hands it the whole thing again.
        await Deploy();
        return Describe(State);
    }

    public async Task<SlotBindingOutcome> BindSlots(Dictionary<string, string> bindings, CancellationToken ct = default)
    {
        await Check("BindSlots",
            new AppExists(Name(), State.Manifest),
            new AppRecordIsUsable(Name(), State.Refused));

        if (bindings is null || bindings.Count == 0)
        {
            return SlotBindingOutcome.Bound(Describe(State));
        }

        var (canonical, refusal) = await ResolveSlotBindingsAsync(State.Manifest!, bindings, ct);
        if (refusal is not null)
        {
            return SlotBindingOutcome.Refused(refusal);
        }

        foreach (var (slot, id) in canonical!)
        {
            if (State.Connections.TryGetValue(slot, out var current) && string.Equals(current, id, StringComparison.Ordinal)) { continue; }
            foreach (var hook in storageHooks)
            { await hook.RequireRebindable(State.BrainScopeId ?? "", Key, slot, ct); }
        }

        if (IsDirectPerson(Context.Caller))
        {
            await ApplySlotBindingsAsync(canonical, pendingPlanIdToClear: null, ct);
            return SlotBindingOutcome.Bound(Describe(State));
        }

        if (Confirmations is null)
        {
            return SlotBindingOutcome.Refused("Only a person can bind connections to this app.");
        }

        await Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain));
        var plan = BuildBindSlotsPlan(State.Manifest!, canonical);
        var immediate = await Confirmations.Request(plan);
        if (immediate is { Approved: true, Skipped: false, Approver.IsHuman: true })
        {
            await ApplySlotBindingsAsync(canonical, pendingPlanIdToClear: null, ct);
            return SlotBindingOutcome.Bound(Describe(State));
        }

        if (immediate is not null)
        {
            var reason = immediate.Reason == ConfirmationDenyReason.None
                ? "Connection binding was denied."
                : $"Connection binding was denied ({immediate.Reason}).";
            return SlotBindingOutcome.Refused(reason);
        }

        var next = State.Clone();
        var pending = new PendingSlotBinding(plan.Id, StringEntry.From(canonical), clock.GetUtcNow());
        next.PendingSlotBindings = [.. next.PendingSlotBindings.Where(p => !string.Equals(p.PlanId, plan.Id, StringComparison.Ordinal)), pending];
        await Store(next);
        return SlotBindingOutcome.Pending(plan.Id, "Waiting for a person to approve binding these connections.");
    }

    [DigitalBrain.Neurons.On]
    public async Task OnConfirmationResolved(ConfirmationResolved resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var pending = State.PendingSlotBindings.FirstOrDefault(p => string.Equals(p.PlanId, resolved.PlanId, StringComparison.Ordinal));
        if (pending is null) { return; }

        ConfirmationResult? authoritative = null;
        if (Confirmations is not null)
        {
            authoritative = await Confirmations.Outcome(resolved.PlanId);
        }

        if (authoritative is { Approved: true, Skipped: false, Approver.IsHuman: true }
            && State.Manifest is not null
            && State.Refused is null)
        {
            var bindings = StringEntry.ToMap(pending.Bindings);
            foreach (var (slot, id) in bindings)
            {
                if (State.Connections.TryGetValue(slot, out var current) && string.Equals(current, id, StringComparison.Ordinal)) { continue; }
                foreach (var hook in storageHooks)
                { await hook.RequireRebindable(State.BrainScopeId ?? "", Key, slot, CancellationToken.None); }
            }

            await ApplySlotBindingsAsync(bindings, pendingPlanIdToClear: resolved.PlanId, CancellationToken.None);
            return;
        }

        var cleared = State.Clone();
        cleared.PendingSlotBindings = [.. cleared.PendingSlotBindings.Where(p => !string.Equals(p.PlanId, resolved.PlanId, StringComparison.Ordinal))];
        await Store(cleared);
    }

    private async Task ApplySlotBindingsAsync(
        IReadOnlyDictionary<string, string> canonicalBindings,
        string? pendingPlanIdToClear,
        CancellationToken ct)
    {
        var next = State.Clone();
        foreach (var (slot, key) in canonicalBindings)
        {
            next.PutConnection(slot, key);
        }
        if (pendingPlanIdToClear is not null)
        {
            next.PendingSlotBindings = [.. next.PendingSlotBindings.Where(p => !string.Equals(p.PlanId, pendingPlanIdToClear, StringComparison.Ordinal))];
        }
        AdvanceProgram(next);
        await Persist(next);
        await SyncConnectionBindings(next, ct);
        await Deploy();
    }

    private async Task<(Dictionary<string, string>? Canonical, string? Refusal)> ResolveSlotBindingsAsync(
        AppManifest manifest,
        IReadOnlyDictionary<string, string> requested,
        CancellationToken ct)
    {
        var brain = BrainId();
        var canonical = new Dictionary<string, string>(requested.Count, StringComparer.Ordinal);
        ConnectionSummary[]? catalogConnections = null;
        var catalogLoaded = false;

        async Task<ConnectionSummary[]?> LoadCatalogAsync()
        {
            if (catalogLoaded) { return catalogConnections; }
            catalogLoaded = true;
            if (ConnectionCatalog is null) { return null; }
            try
            {
                catalogConnections = await ConnectionCatalog.ListConnections().WaitAsync(ct);
            }
            catch (InvalidOperationException error)
            {
                (logger ?? services?.GetService<ILogger<AppNeuron>>())?.LogDebug(
                    error, "Connection catalog unavailable while validating slot bindings for app {App}.", Key);
            }
            return catalogConnections;
        }

        foreach (var (slotName, rawTarget) in requested)
        {
            if (string.IsNullOrWhiteSpace(slotName) || string.IsNullOrWhiteSpace(rawTarget))
            {
                return (null, "Every slot binding requires a slot name and connection key.");
            }

            var slot = manifest.Slots.FirstOrDefault(s => string.Equals(s.Name, slotName, StringComparison.Ordinal));
            if (slot is null)
            {
                return (null, $"This app has no connection named {slotName}.");
            }

            var target = rawTarget.Trim();
            if (ConnectionKeys.TryParse(target, out var parsed))
            {
                if (!string.Equals(parsed.BrainId, brain, StringComparison.Ordinal))
                {
                    return (null, $"Connection '{target}' does not belong to this brain.");
                }

                var connections = await LoadCatalogAsync();
                if (connections is not null)
                {
                    var matched = connections.FirstOrDefault(c => string.Equals(c.Neuron, target, StringComparison.Ordinal));
                    if (matched is not null)
                    {
                        var satisfies = slot.Capability is { Length: > 0 } cap
                            ? AppCapabilities.Satisfies(matched.Provides, cap)
                            : string.Equals(matched.Kind, slot.Connector, StringComparison.Ordinal);
                        if (!satisfies)
                        {
                            return (null, $"Connection '{target}' does not satisfy {slot.Binds} for slot '{slot.Name}'.");
                        }
                    }
                    else if (slot.Capability is null && !string.Equals(parsed.ConnectorId, slot.Connector, StringComparison.Ordinal))
                    {
                        return (null, $"Connection '{target}' does not match connector '{slot.Connector}' for slot '{slot.Name}'.");
                    }
                }
                else if (slot.Capability is null && !string.Equals(parsed.ConnectorId, slot.Connector, StringComparison.Ordinal))
                {
                    return (null, $"Connection '{target}' does not match connector '{slot.Connector}' for slot '{slot.Name}'.");
                }

                canonical[slot.Name] = target;
                continue;
            }

            // Short connection id: resolve through the live connection catalog first, then fall back to explicit connector slots.
            var listed = await LoadCatalogAsync();
            if (listed is not null)
            {
                var matched = listed
                    .Where(c => string.Equals(c.Id, target, StringComparison.Ordinal)
                        && (slot.Capability is { Length: > 0 } cap
                            ? AppCapabilities.Satisfies(c.Provides, cap)
                            : string.Equals(c.Kind, slot.Connector, StringComparison.Ordinal)))
                    .OrderByDescending(c => string.Equals(c.Status, "connected", StringComparison.Ordinal))
                    .FirstOrDefault();
                if (matched is not null)
                {
                    canonical[slot.Name] = matched.Neuron;
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(slot.Connector))
            {
                canonical[slot.Name] = ConnectionKeys.For(brain, slot.Connector, target);
                continue;
            }

            return (null, $"Choose a {slot.Binds} connection for {slot.Name} using its canonical connection key.");
        }

        return (canonical, null);
    }

    private ConfirmationPlan BuildBindSlotsPlan(AppManifest manifest, IReadOnlyDictionary<string, string> canonical)
    {
        var ordered = canonical.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var summary = string.Join(", ", ordered.Select(p => $"{p.Key} -> {p.Value}"));
        var hashInput = $"{BrainId()}|{Name()}|{summary}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..16];
        var planId = $"app-bind:{Name()}:{digest}";
        if (planId.Length > 128) { planId = planId[..128]; }

        var appTitle = string.IsNullOrWhiteSpace(manifest.Title) ? Name() : manifest.Title;
        return new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: false,
            Title: $"Connect tools to {appTitle}",
            Body: $"Allow {appTitle} to access: {summary}.",
            ApproveLabel: "Allow",
            DenyLabel: "Deny",
            TargetLabel: appTitle)
        {
            RequiresPerson = true,
            Kind = "app.bind-slots",
            Subject = $"{Name()}:{string.Join(",", ordered.Select(p => p.Key))}",
        };
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
        await Context.Wires.EnsureBrainWide(typeof(DigitalBrain.Flows.FlowDone));
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
            await Deployed(null, null);
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
        => IntoChat.OS.Host.Primitives.AppRef.TryParse(Id.Brain, Key, out var app)
            ? app.Name
            : IntoChat.OS.Host.Primitives.AppRef.TryParse(Id.Brain, Id.Key, out var alt)
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
