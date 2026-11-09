using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// Wraps the silo's synapse host for Ino. A deploy whose step key ({alias}:{source and bindings hash}) already landed does not reach the host
// again, so a re-advanced task step is not rerun. Failures come back as readable text.
[DigitalBrain.Neuron("ino.synapses")]
public sealed partial class SynapseStudioNeuron : DigitalBrain.Neuron<SynapseStudioState>, ISynapseStudio
{
    internal const string Unavailable = "Synapse hosting is not available in this brain.";

    private ISynapseHost? Host => Context.Services.GetService<ISynapseHost>();

    public Task<InoToolResult> Check(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Context.Services.GetService<SynapseCompiler>() is not { } compiler) { return Task.FromResult(new InoToolResult(Unavailable, Ok: false)); }
        var build = compiler.Compile(new SynapseSource("check", new Dictionary<string, string> { ["unit.cs"] = source }, null));
        return Task.FromResult(new InoToolResult(build.Diagnostics.Count == 0
            ? "No diagnostics; the unit compiles."
            : (build.Success ? "Compiles with diagnostics:\n" : "Does not compile:\n") + Describe(build.Diagnostics), Ok: build.Success));
    }

    public async Task<InoToolResult> Deploy(string alias, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var result = await DeployFiles(alias, ISynapseStudio.SingleFile(alias, source), new Dictionary<string, string>());
        return new InoToolResult(result.Text, Ok: result.Ok);
    }

    // The bindings travel with the source (SynapseSource.Bindings), so restore and re-deploy bind the same connections. A step key
    // already landed is answered from the installs neuron: installed reads Unchanged, a waiting ask reads Pending with its plan.
    public async Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(bindings);
        if (files.Count == 0) { return Failed($"Could not deploy {alias}: no files."); }
        if (Host is not { } host) { return Failed(Unavailable); }
        // Until the framework's compiler skips them, spec.md and scenarios.json stay here and only the C# reaches the host.
        var code = UnitFiles.Code(files);
        if (code.Count == 0) { return Failed($"Could not deploy {alias}: no .cs files."); }
        if (ReservedSignal(alias, code) is { } reserved) { return Failed($"Could not deploy {alias}: {reserved} signals belong to the {reserved} unit."); }
        var sidecars = files.Where(file => !code.ContainsKey(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        var step = ISynapseStudio.StepKey(alias, code, bindings);
        if (State.Deployed.TryGetValue(step, out var done) && await Landed(alias, done) is { } landed)
        {
            if (!SameSidecars(alias, sidecars)) { await Save(State with { Sidecars = WithSidecars(alias, sidecars) }, new SynapseStudioChanged(alias)); }
            return landed;
        }
        DeployOutcome outcome;
        try
        {
            var bound = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                [alias] = new Dictionary<string, string>(bindings, StringComparer.Ordinal),
            };
            outcome = await host.Deploy(Id.Brain, new SynapseSource(alias, new Dictionary<string, string>(code, StringComparer.Ordinal), null, bound),
                new Dictionary<string, IReadOnlyDictionary<string, string>>(), CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Failed($"Could not deploy {alias}: {error.Message}");
        }

        if (outcome.Status is DeployStatus.Failed or DeployStatus.Refused)
        {
            return Failed($"Could not deploy {alias}: {outcome.Reason ?? outcome.Status.ToString()}"
                + (outcome.Diagnostics.Count == 0 ? "" : "\n" + Describe(outcome.Diagnostics)), outcome);
        }

        var deployed = Without(alias);
        deployed[step] = outcome.Hash ?? "";
        await Save(State with { Deployed = deployed, Sidecars = WithSidecars(alias, sidecars) }, new SynapseStudioChanged(alias));
        return new UnitDeployResult(true, $"{alias} is {outcome.Status.ToString().ToLowerInvariant()} (hash {outcome.Hash}).", outcome.Status, outcome.Hash, outcome.Installs);
    }

    // A circle unit's signals ({unit}.join, {unit}.leave, {unit}.message) are its own: the room trusts join/leave as the circle's and
    // message's "by" as the host's stamp, which only a view activation carries today (a unit-to-unit publish could forge it until the
    // framework stamps the publisher). So no other unit may name one in its code (a view emit, a declared signal). Other units' signals
    // (x.polled) are open. The unit name a unit's code reserves, or null.
    internal static readonly string[] CircleOnlySignals = ["join", "leave", "message"];

    internal static string? ReservedSignal(string alias, IReadOnlyDictionary<string, string> code)
        => ShippedApps.CircleUnits.FirstOrDefault(unit => unit != alias
            && CircleOnlySignals.Any(signal => code.Values.Any(text => text.Contains($"\"{unit}.{signal}\"", StringComparison.Ordinal))));

    private static UnitDeployResult Failed(string text, DeployOutcome? outcome = null)
        => new(false, text, outcome?.Status ?? DeployStatus.Failed, outcome?.Hash, outcome?.Installs ?? new Dictionary<string, DigitalBrain.InstallOutcome>());

    // The outcome of a step key that already landed, or null when the installs neuron knows nothing of the alias (deploy again).
    private async Task<UnitDeployResult?> Landed(string alias, string hash)
    {
        var installs = Context.Get<IInstalls>(new NeuronId(Id.Brain, "synapse.installs", "default"));
        DigitalBrain.InstallOutcome install;
        try
        {
            // A pending ask comes first: a record left by an earlier install does not answer a redeploy with wider needs.
            if ((await installs.Pending(CancellationToken.None)).FirstOrDefault(pending => pending.Alias == alias) is { } pending)
            { install = new DigitalBrain.InstallOutcome(DigitalBrain.InstallStatus.Pending, pending.Sentence, pending.PlanId); }
            else if (await installs.Describe(alias, CancellationToken.None) is not null) { install = new DigitalBrain.InstallOutcome(DigitalBrain.InstallStatus.Unchanged, "", null); }
            else { return null; }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A brain hosting no installs neuron installs nothing: the unit is deployed and that is all.
            return new UnitDeployResult(true, $"{alias} is already deployed (hash {hash}).", DeployStatus.Unchanged, hash, new Dictionary<string, DigitalBrain.InstallOutcome>());
        }
        return new UnitDeployResult(true, $"{alias} is already deployed (hash {hash}).", DeployStatus.Unchanged, hash,
            new Dictionary<string, DigitalBrain.InstallOutcome>(StringComparer.Ordinal) { [alias] = install });
    }

    public async Task<InoToolResult> Retire(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if (Host is not { } host) { return new InoToolResult(Unavailable, Ok: false); }
        try
        {
            var outcome = await host.Retire(Id.Brain, alias, CancellationToken.None);
            if (outcome.Status != DeployStatus.Retired) { return new InoToolResult($"Could not retire {alias}: {outcome.Reason ?? outcome.Status.ToString()}", Ok: false); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not retire {alias}: {error.Message}", Ok: false);
        }

        await Save(State with { Deployed = Without(alias), Sidecars = WithSidecars(alias, null) }, new SynapseStudioChanged(alias));
        return new InoToolResult($"Retired {alias}.");
    }

    // The host lists only loaded units, and loads a stored one lazily on its alias's first miss. A scheduled unit may see no miss
    // after a restart, so List first restores every stored unit the host has not loaded. A unit stored before bindings were kept
    // (Bindings null) that declares a [Slot] cannot be bound again: it is reported Refused, never restored, and must be rebuilt.
    public async Task<DeployedSource[]> List()
    {
        if (Host is not { } host) { return []; }
        var live = await host.List(Id.Brain, CancellationToken.None);
        if (Context.Services.GetService<ISourceStore>() is not { } store) { return [.. live]; }
        var records = await store.List(Id.Brain, CancellationToken.None);
        var stale = records.Where(RebuildRequired).ToArray();
        if (Context.Services.GetService<DigitalBrain.Spi.ISynapseRestorer>() is { } restorer)
        {
            var missing = records
                .Where(record => record.Aliases.Count > 0 && !stale.Contains(record) && live.All(source => source.SourceKey != record.SourceKey)).ToArray();
            foreach (var record in missing) { await restorer.TryRestore(Id.Brain, record.Aliases[0], CancellationToken.None); }
            if (missing.Length > 0) { live = await host.List(Id.Brain, CancellationToken.None); }
        }

        return [.. live.Where(source => stale.All(record => record.SourceKey != source.SourceKey)),
            .. stale.Select(record => new DeployedSource(record.SourceKey, record.Hash, record.Aliases, DeployStatus.Refused, null, record.DeployedAt))];
    }

    // The stored record predates kept bindings and its source declares a slot: nothing says what the slot was bound to.
    internal static bool RebuildRequired(SourceRecord record)
        => record.Bindings is null && record.Files.Values.Any(text => SlotDeclaration().IsMatch(text));

    [System.Text.RegularExpressions.GeneratedRegex(@"\[\s*(?:DigitalBrain\.)?Slot(?:Attribute)?\s*(?:\]|\()")]
    private static partial System.Text.RegularExpressions.Regex SlotDeclaration();

    public Task<Dictionary<string, string>> Sidecars(string alias)
        => Task.FromResult(State.Sidecars?.TryGetValue(alias, out var files) == true
            ? new Dictionary<string, string>(files, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal));

    public async Task<Dictionary<string, string>> SourceFiles(string alias)
        => Context.Services.GetService<ISourceStore>() is { } store && await store.Get(Id.Brain, alias, CancellationToken.None) is { } record
            ? new Dictionary<string, string>(record.Files, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);

    private bool SameSidecars(string alias, Dictionary<string, string> sidecars)
        => State.Sidecars?.TryGetValue(alias, out var known) == true && known.Count == sidecars.Count
            && sidecars.All(file => known.TryGetValue(file.Key, out var text) && text == file.Value);

    // The sidecars with alias's replaced by files (removed when null or empty).
    private Dictionary<string, Dictionary<string, string>> WithSidecars(string alias, Dictionary<string, string>? files)
    {
        var next = (State.Sidecars ?? []).Where(entry => entry.Key != alias)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (files is { Count: > 0 }) { next[alias] = files; }
        return next;
    }

    private Dictionary<string, string> Without(string alias)
        => State.Deployed.Where(entry => !entry.Key.StartsWith(alias + ":", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static string Describe(IEnumerable<SynapseDiagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}"));
}
