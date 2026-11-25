using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Units;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

// One sentence becomes one unit: author (the Creator's Author), draft (the Builder), verify (the unit's scenarios on fakes), deploy
// (bind each slot to a connection, then the studio) and install (the host's install outcome; consent to new needs is the only
// approval). Each step is idempotent on rerun: author by goal hash, draft by unit hash, deploy through the studio's step key.
// Deploy waits External when a slot has no connection or several fit, by one rule whether it runs or re-checks after a wait: none
// is re-checked on ConnectionsChanged, on activation and every minute; several raise one plan per candidate connection (a plan is
// yes/no and carries no options), and the approved one binds. Install waits External on the installs neuron's own plan (its id on
// the FlowWaiting), ended by Installed or InstallDenied.
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class BuildFlow : Flow<BuildInner>, IFlowStarter, IBuildChoices
{
    public const string Alias = BuildInner.Flow;

    internal const string Recheck = "build-recheck";

    private static readonly string[] Names = ["author", "draft", "verify", "deploy", "install"];

    private readonly IDrafter? drafter;
    private readonly IConfirmations? confirmations;
    private readonly SynapseCompiler? compiler;
    private readonly ICapabilityConnections? connections;
    private readonly IConnectionHosts? hosts;
    private readonly ILogger<BuildFlow>? logger;

    public BuildFlow(
        IDrafter? drafter = null,
        IConfirmations? confirmations = null,
        SynapseCompiler? compiler = null,
        ICapabilityConnections? connections = null,
        IConnectionHosts? hosts = null,
        ILogger<BuildFlow>? logger = null)
    {
        this.drafter = drafter;
        this.confirmations = confirmations;
        this.compiler = compiler;
        this.connections = connections;
        this.hosts = hosts;
        this.logger = logger;
    }

    // Choice plans this activation is asking, with their cancellation tokens, and the one-use proofs handed their detached asks.
    private readonly Dictionary<string, CancellationTokenSource> _asking = new(StringComparer.Ordinal);
    private readonly HashSet<string> _proofs = new(StringComparer.Ordinal);

    protected override IReadOnlyList<string> Steps => Names;

    // The base Start saves and returns; the steps run after it, so the caller does not wait for the Author.
    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<BuildInner>(innerJson, BuildInner.Json) ?? new BuildInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Names], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, BuildInner.Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    // The alias when the goal starts with "change <alias>:" and <alias> is a valid unit alias, else null.
    internal static string? ChangeAlias(string goal)
    {
        var trimmed = (goal ?? "").Trim();
        if (!trimmed.StartsWith("change ", StringComparison.Ordinal)) { return null; }
        var colon = trimmed.IndexOf(':', 7);
        if (colon < 0) { return null; }
        var alias = trimmed[7..colon].Trim();
        try { Creator.ValidateAlias(alias); return alias; }
        catch (InvalidDataException) { return null; }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Wires.EnsureBrainWide(typeof(Installed), typeof(InstallDenied), typeof(ConnectionsChanged));
        await base.OnActivate(cancellationToken);
        if (WaitingOnConnection()) { await CheckConnections(); }
        else if (WaitingOnInstall()) { await CheckInstall(); }
    }

    // An app build reports each finished step on the app's activity. A failure is not recorded here: the app fails its attempt on
    // the flow's FlowDone.
    protected override async Task<StepResult<BuildInner>> Run(string step, BuildInner inner, CancellationToken ct)
    {
        var result = await RunStep(step, inner, ct);
        if (inner.AppKey is not null && result.Wait is null && step != "install")
        {
            await App(inner).Progress("working", step switch
            {
                "author" => $"author: {inner.Alias}",
                "draft" => $"draft: {inner.Files?.Count ?? 0} files",
                "verify" => "verify: the scenarios pass",
                _ => $"{step}: done",
            });
        }
        return result;
    }

    private async Task<StepResult<BuildInner>> RunStep(string step, BuildInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "author":
                {
                    var goalHash = Sha(inner.Goal);
                    if (inner.UnitJson is null || inner.GoalHash != goalHash)
                    {
                        var changing = ChangeAlias(inner.Goal);
                        if (changing is not null) { inner.Existing ??= await Current(changing, ct); }
                        // The Author's model call runs off the flow's scheduler.
                        var unit = await Task.Run(() => (drafter ?? throw new InvalidOperationException("No drafter configured.")).Author(Id.Brain.Value, inner.Goal, inner.Existing, ct), ct);
                        if (changing is not null && unit.Alias != changing) { throw new FlowFailed("author", $"a change keeps the alias: the Author answered {unit.Alias} for a change of {changing}"); }
                        inner.Existing ??= await Current(unit.Alias, ct);
                        try { unit = unit with { Scenarios = [.. KeptScenarios.Merge(inner.Existing, unit.Scenarios, inner.Goal)] }; }
                        catch (InvalidDataException error) { throw new FlowFailed("author", error.Message); }
                        inner.UnitJson = JsonSerializer.Serialize(unit, BuildInner.Json);
                        inner.GoalHash = goalHash;
                        inner.Alias = unit.Alias;
                    }
                    return inner.Advance(new FlowSubject(Id.ToString(), inner.Alias!));
                }
            case "draft":
                {
                    var unitHash = Sha(inner.UnitJson ?? "");
                    if (inner.Files is { Count: > 0 } && inner.UnitHash == unitHash) { return inner.Advance(); }
                    var unit = inner.Unit();
                    // "Change it": a unit already deployed under the alias is drafted over its current files.
                    inner.Existing ??= await Current(unit.Alias, ct);
                    var draft = await Task.Run(() => (drafter ?? throw new InvalidOperationException("No drafter configured.")).Draft(Id.Brain.Value, unit, inner.Existing, ct), ct);
                    inner.Files = draft.Files;
                    inner.UnitHash = unitHash;
                    return inner.Advance();
                }
            case "verify":
                await Task.Run(() => Verify(inner, ct), ct);
                return inner.Advance();
            case "deploy":
                {
                    if (inner.Deployed) { return inner.Advance(); }
                    var unit = inner.Unit();
                    if (Bind(inner, await Resolve(unit.Needs, inner.Chosen, ct)))
                    {
                        // The answers come back through the mailbox, so after the base has saved this wait.
                        RaiseChoices(inner);
                        await Context.Reminders.Start(Recheck, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                        return inner.WaitExternal(step, null, MissingNote(inner));
                    }
                    await DeployUnit(inner, unit);
                    return inner.Advance();
                }
            case "install":
                return await Install(inner);
            default:
                throw new FlowFailed(step, $"Unknown step {step}.");
        }
    }

    // The one binding rule, for a deploy and for a re-check after its wait: stores what resolved, and returns whether deploy must
    // wait (a slot has no connection, or several fit and the person has not chosen). Several get one plan per candidate, its id
    // derived from the flow, slot and connection, so a rerun or re-raise after a crash asks the same plan again.
    private bool Bind(BuildInner inner, Resolution resolved)
    {
        inner.Bindings = resolved.Bound;
        inner.Missing = resolved.Missing.Count == 0 ? null : string.Join("; ", resolved.Missing);
        inner.Choices = resolved.Several.Count == 0 ? null : resolved.Several;
        if (inner.Missing is not null) { inner.ChoicePlans = null; return true; }
        if (inner.Choices is null) { inner.ChoicePlans = null; return false; }
        var plans = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (slot, candidates) in inner.Choices)
        {
            foreach (var key in candidates) { plans[Sha($"{Id}\0choice\0{slot}\0{key}")] = [slot, key]; }
        }
        inner.ChoicePlans = plans;
        return true;
    }

    protected override async Task OnReminder(string name, CancellationToken cancellationToken)
    {
        if (name != Recheck) { await base.OnReminder(name, cancellationToken); return; }
        if (WaitingOnConnection()) { await CheckConnections(); }
        else if (WaitingOnInstall()) { await CheckInstall(); }
        else { await StopRecheck(); }
    }

    // Raises each choice plan this activation is not already asking (activation, reminders and ConnectionsChanged re-raise them;
    // the confirmations neuron re-joins a plan id the same requester raised).
    private void RaiseChoices(BuildInner inner)
    {
        if (inner.ChoicePlans is not { Count: > 0 } plans) { return; }
        foreach (var (planId, choice) in plans)
        {
            if (_asking.ContainsKey(planId)) { continue; }
            var cts = new CancellationTokenSource();
            _asking[planId] = cts;
            var plan = ChoicePlan(planId, choice, plans.Count(p => p.Value[0] == choice[0]), inner, State.Title);
            _ = AskChoice(plan, cts.Token);
        }
    }

    // The card names the build by the person's own goal where there is one; every model-authored name (alias, slot) and the
    // connection key is clamped to one short line, so an authored unit cannot write the approval card's text.
    internal static ConfirmationPlan ChoicePlan(string planId, string[] choice, int count, BuildInner inner, string title)
    {
        var what = Clean(string.IsNullOrWhiteSpace(inner.Goal) ? inner.Alias ?? title : inner.Goal);
        var (slot, key) = (Clean(choice[0]), Clean(choice[1]));
        return new ConfirmationPlan(planId, ConfirmationAction.Other, Destructive: false, Title: $"Use {key} for {what}?",
            Body: $"Slot {slot} of {what}: {count} connections fit. Approve to bind {key}; the others are withdrawn.",
            ApproveLabel: "Use this connection", DenyLabel: "Not this one", TargetLabel: key)
        { RequiresPerson = true };
    }

    internal const int MaxCardName = 64;

    // Control characters and line breaks become spaces, runs of white space one space, and the result at most 64 characters.
    internal static string Clean(string text)
    {
        var line = new StringBuilder(Math.Min(text.Length, MaxCardName));
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            { space = line.Length > 0; continue; }
            if (space) { line.Append(' '); space = false; }
            line.Append(c);
        }
        var clean = line.ToString();
        return clean.Length <= MaxCardName ? clean : string.Concat(clean.AsSpan(0, MaxCardName - 1), "…");
    }

    // Raises the plan without awaiting it in the turn; the answer comes back through the flow's own mailbox with a one-use proof.
    private async Task AskChoice(ConfirmationPlan plan, CancellationToken ct)
    {
        bool approved;
        try { approved = confirmations is not null && (await confirmations.Confirm(plan, ct)).Outcome == ConfirmationOutcome.Approved; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CleanupAsking(plan.Id);
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            BuildLog.ChoiceAskFailed((ILogger?)logger ?? OsConsoleLog.For<BuildFlow>(), error, Id.ToString(), plan.Id);
            CleanupAsking(plan.Id);
            return;
        }
        var proof = Guid.NewGuid().ToString("N");
        _proofs.Add(proof);
        try { await Context.Get<IBuildChoices>(Id).ChoiceAnswered(plan.Id, approved, proof); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _proofs.Remove(proof);
            ((ILogger?)logger ?? OsConsoleLog.For<BuildFlow>()).LogSwallowed(error, $"ChoiceAnswered failed for build plan '{plan.Id}'.");
        }
        finally { CleanupAsking(plan.Id); }
    }

    private void CleanupAsking(string planId)
    {
        if (_asking.Remove(planId, out var cts)) { cts.Dispose(); }
    }

    // An approved candidate binds its slot and withdraws the slot's other plans; once every slot is chosen, deploy ends its wait and
    // install deploys with the bindings. Denying every candidate of a slot cancels the flow.
    public async Task ChoiceAnswered(string planId, bool approved, string proof)
    {
        // Only the flow's own ask answers a choice: a call without a live proof is ignored.
        if (!_proofs.Remove(proof)) { return; }
        if (!WaitingOnConnection() || State.Inner.ChoicePlans is not { } plans || !plans.Remove(planId, out var choice)) { return; }
        var (slot, key) = (choice[0], choice[1]);
        if (approved)
        {
            State.Inner.Chosen = new Dictionary<string, string>(State.Inner.Chosen ?? [], StringComparer.Ordinal) { [slot] = key };
            foreach (var other in plans.Where(plan => plan.Value[0] == slot).Select(plan => plan.Key).ToArray())
            {
                plans.Remove(other);
                if (_asking.Remove(other, out var cts))
                {
                    try { await cts.CancelAsync(); }
                    finally { cts.Dispose(); }
                }
                try { await Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain)).Withdraw(other); }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or UnauthorizedAccessException)
                {
                    ((ILogger?)logger ?? OsConsoleLog.For<BuildFlow>()).LogSwallowed(error, "Withdrawing candidate choice plan resolution.");
                }
            }
        }
        else if (!plans.Values.Any(plan => plan[0] == slot) && State.Inner.Chosen?.ContainsKey(slot) != true)
        {
            await StopRecheck();
            await Resume("deploy", WaitOutcome.Denied, null);
            return;
        }
        if (plans.Count > 0) { await Save(State, [StillWaiting()]); return; }
        foreach (var (chosenSlot, chosenKey) in State.Inner.Chosen ?? []) { (State.Inner.Bindings ??= [])[chosenSlot] = chosenKey; }
        State.Inner.ChoicePlans = null;
        await StopRecheck();
        await Resume("deploy", WaitOutcome.Arrived, null);
    }

    private async Task Verify(BuildInner inner, CancellationToken ct)
    {
        var files = inner.Files is { Count: > 0 } drafted ? drafted : throw new FlowFailed("verify", "the draft has no files");
        var unit = inner.Unit();
        var comp = compiler ?? throw new FlowFailed("verify", SynapseStudioNeuron.Unavailable);
        var check = await BuilderTools.Check(comp, unit.Alias, files, unit.Needs, unit.Scenarios, ct);
        if (check.Passed) { return; }
        var transcript = new StringBuilder("The unit did not pass verify.");
        foreach (var diagnostic in check.Diagnostics) { transcript.Append('\n').Append($"{diagnostic.File}({diagnostic.Line}) {diagnostic.Id}: {diagnostic.Message}"); }
        foreach (var failure in check.Failures) { transcript.Append('\n').Append(failure); }
        throw new FlowFailed("verify", transcript.ToString());
    }

    private async Task DeployUnit(BuildInner inner, AuthoredUnit unit)
    {
        var bindings = new Dictionary<string, string>(inner.Bindings ?? [], StringComparer.Ordinal);
        foreach (var (slot, key) in inner.Chosen ?? []) { bindings[slot] = key; }
        var result = await Studio().DeployFiles(unit.Alias, inner.Files!, bindings);
        if (!result.Ok) { throw new FlowFailed("deploy", result.Text); }
        var install = result.InstallOf(unit.Alias);
        inner.Bindings = bindings;
        inner.Choices = null;
        inner.Deployed = true;
        inner.Hash = result.Hash;
        inner.Install = install.Status;
        inner.PlanId = install.Status == DigitalBrain.Units.InstallStatus.Pending ? install.Reason : null;
        inner.Sentence = install.Sentence;
        if (install.Status is DigitalBrain.Units.InstallStatus.Refused or DigitalBrain.Units.InstallStatus.Denied) { inner.Missing = install.Reason; }
    }

    // A deploy that waited ends with its bindings in Inner, not deployed: install deploys first. A Pending install waits External on
    // the installs neuron's plan; the base saves no plan id for an External wait, so a second FlowWaiting names it for the index.
    private async Task<StepResult<BuildInner>> Install(BuildInner inner)
    {
        if (!inner.Deployed) { await DeployUnit(inner, inner.Unit()); }
        switch (inner.Install)
        {
            case DigitalBrain.Units.InstallStatus.Pending:
                inner.NeedsHash ??= (await Installs().Pending(CancellationToken.None)).FirstOrDefault(pending => pending.PlanId == inner.PlanId)?.NeedsHash;
                await Report(inner, DigitalBrain.Units.InstallStatus.Pending);
                await Context.Reminders.Start(Recheck, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                return inner.WaitExternal("install", inner.PlanId, new FlowWaiting(Id.ToString(), "install", WaitReason.External, inner.PlanId));
            case DigitalBrain.Units.InstallStatus.Refused or DigitalBrain.Units.InstallStatus.Denied:
                throw new FlowFailed("install", $"{inner.Alias} was not installed: {inner.Missing ?? inner.Install.ToString()}");
            default:
                await Report(inner, inner.Install ?? DigitalBrain.Units.InstallStatus.Unchanged);
                await StopRecheck();
                return inner.Advance();
        }
    }

    // The installs neuron journals Installed when the person approves; the record is read back, since any neuron can journal it.
    [On]
    public async Task OnInstalled(Installed installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (!WaitingOnInstall() || installed.Alias != State.Inner.Alias) { return; }
        if (State.Inner.NeedsHash is { } needs && installed.NeedsHash != needs) { return; }
        await CheckInstall();
    }

    [On]
    public async Task OnInstallDenied(InstallDenied denied)
    {
        ArgumentNullException.ThrowIfNull(denied);
        if (!WaitingOnInstall() || denied.PlanId != State.Inner.PlanId) { return; }
        await CheckInstall();
    }

    [On]
    public Task OnConnectionsChanged(ConnectionsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return WaitingOnConnection() ? CheckConnections() : Task.CompletedTask;
    }

    private bool WaitingOnInstall() => State is { Status: FlowStatus.Waiting, Waiting: { Reason: WaitReason.External, Step: "install" } };

    private bool WaitingOnConnection() => State is { Status: FlowStatus.Waiting, Waiting: { Reason: WaitReason.External, Step: "deploy" } };

    private IInstalls Installs() => Context.Get<IInstalls>(new NeuronId(Id.Brain, "synapse.installs", "default"));

    // The flow's own ask still pending waits on. Once it is gone, a record carrying the needs it asked ends the wait and the app goes
    // live; no record, or one left by an earlier install of the alias (other needs), means the ask was denied or superseded.
    private async Task CheckInstall()
    {
        var alias = State.Inner.Alias ?? "";
        var installs = Installs();
        if ((await installs.Pending(CancellationToken.None)).Any(pending => pending.PlanId == State.Inner.PlanId)) { return; }
        if (await installs.Describe(alias, CancellationToken.None) is { } record && (State.Inner.NeedsHash is not { } needs || record.NeedsHash == needs))
        {
            State.Inner.Install = DigitalBrain.Units.InstallStatus.Installed;
            await Report(State.Inner, DigitalBrain.Units.InstallStatus.Installed);
            await StopRecheck();
            await Resume("install", WaitOutcome.Arrived, null);
            return;
        }
        State.Inner.Install = DigitalBrain.Units.InstallStatus.Denied;
        await StopRecheck();
        await Resume("install", WaitOutcome.Denied, null);
    }

    // The deploy wait re-checked by the same rule as the step: still missing waits on; several (newly) fitting raises the choice
    // plans; all bound ends the wait, and install deploys.
    private async Task CheckConnections()
    {
        var before = State.Inner.ChoicePlans?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [];
        var missing = State.Inner.Missing;
        if (Bind(State.Inner, await Resolve(State.Inner.Unit().Needs, State.Inner.Chosen, CancellationToken.None)))
        {
            var after = State.Inner.ChoicePlans?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [];
            if (!before.SequenceEqual(after) || missing != State.Inner.Missing) { await Save(State, [StillWaiting(), MissingNote(State.Inner)]); }
            RaiseChoices(State.Inner);
            return;
        }
        await StopRecheck();
        await Resume("deploy", WaitOutcome.Arrived, null);
    }

    private Task StopRecheck() => Context.Reminders.Stop(Recheck);

    // The flow's detail while deploy waits: what no connection satisfies ("connect http@1 for api.x.com"), cleared once it is none.
    private FlowStep MissingNote(BuildInner inner) => new(Id.ToString(), "deploy", StepOutcome.Interrupted, inner.Missing);

    // What the deploy wait waits for changed (a connection arrived, a choice was answered): the same wait, announced again.
    private FlowWaiting StillWaiting() => new(Id.ToString(), "deploy", WaitReason.External, null);

    internal sealed record Resolution(Dictionary<string, string> Bound, Dictionary<string, string[]> Several, List<string> Missing);

    // Per slot: the brain's connected connections providing the slot's capability whose allow-list covers every host it needs. One
    // binds, as does the one the person chose while it still fits; several are asked; none is missing.
    private async Task<Resolution> Resolve(UnitNeeds needs, IReadOnlyDictionary<string, string>? chosen, CancellationToken ct)
    {
        var resolution = new Resolution(new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string[]>(StringComparer.Ordinal), []);
        if (needs.Slots.Count == 0) { return resolution; }
        var connectionsList = connections is not null ? await connections.ListInOrder(Id.Brain, ct) : [];
        foreach (var slot in needs.Slots)
        {
            var capability = CapabilityOf(slot.Capability);
            var fitting = new List<string>();
            foreach (var connection in connectionsList.Where(c => c.Connected && c.Provides.Any(provided => provided.Satisfies(capability))))
            {
                if (slot.Hosts.Count > 0)
                {
                    var allowed = hosts is null ? null : await hosts.AllowedHosts(Id.Brain, connection.Key, ct);
                    if (allowed is null || !slot.Hosts.All(host => IConnectionHosts.Covers(allowed, host))) { continue; }
                }
                fitting.Add(connection.Key);
            }
            if (chosen?.TryGetValue(slot.Name, out var pick) == true && fitting.Contains(pick)) { resolution.Bound[slot.Name] = pick; continue; }
            switch (fitting.Count)
            {
                case 0: resolution.Missing.Add($"connect {capability} for {(slot.Hosts.Count == 0 ? slot.Name : string.Join(", ", slot.Hosts))}"); break;
                case 1: resolution.Bound[slot.Name] = fitting[0]; break;
                default: resolution.Several[slot.Name] = [.. fitting]; break;
            }
        }
        return resolution;
    }

    // "http" is http@1, "tool" tool@1; a versioned id is taken as given.
    internal static CapabilityId CapabilityOf(string capability)
        => CapabilityId.Parse(capability.Contains('@', StringComparison.Ordinal) ? capability : capability + "@1");

    private async Task Report(BuildInner inner, DigitalBrain.Units.InstallStatus status)
    {
        if (inner.AppKey is null || inner.Attempt is not { } attempt) { return; }
        await App(inner).CompleteUnitBuild(attempt, inner.Hash ?? "", status, inner.Sentence ?? "");
    }

    // The deployed source's files under alias plus the studio's sidecars (spec.md, scenarios.json); null when nothing is deployed there.
    private async Task<Dictionary<string, string>?> Current(string alias, CancellationToken ct)
    {
        var sources = Context.Get<ISources>(new NeuronId(Id.Brain, "synapse.sources", "default"));
        var sourceKey = await sources.OwnerOf(alias, ct) ?? alias;
        var deployment = Context.Get<IUnitDeployment>(new NeuronId(Id.Brain, "synapse.deployment", sourceKey));
        var source = await deployment.Source(ct);
        if (source is null) { return null; }
        var files = new Dictionary<string, string>(source.Files, StringComparer.Ordinal);
        foreach (var (path, text) in await Studio().Sidecars(alias)) { files[path] = text; }
        return files;
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private IApp App(BuildInner inner)
    {
        var key = inner.AppKey ?? throw new FlowFailed("install", "the build names no app");
        if (!IntoChat.OS.Host.Primitives.AppRef.TryParse(Id.Brain, key, out var appRef)) { throw new FlowFailed("install", $"{key} is not an app key"); }
        return Context.Get<IApp>(appRef.Neuron);
    }

    private ISynapseStudio Studio() => Context.Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value));
}

internal static partial class BuildLog
{
    // A choice ask that could not be raised is dropped from this activation; the next re-check raises it again.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Build flow {FlowId} could not ask choice plan {PlanId}; it asks again on the next re-check")]
    public static partial void ChoiceAskFailed(ILogger logger, Exception exception, string flowId, string planId);
}
