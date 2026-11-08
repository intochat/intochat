using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// Wraps the silo's synapse host for Ino. A deploy whose step key ({alias}:{source hash}) already landed does not reach the host
// again, so a re-advanced task step is not rerun. Failures come back as readable text.
[DigitalBrain.Neuron("ino.synapses")]
public sealed class SynapseStudioNeuron : DigitalBrain.Neuron<SynapseStudioState>, ISynapseStudio
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
            : (build.Success ? "Compiles with diagnostics:\n" : "Does not compile:\n") + Describe(build.Diagnostics)));
    }

    public Task<InoToolResult> Deploy(string alias, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return DeployFiles(alias, ISynapseStudio.SingleFile(alias, source));
    }

    public async Task<InoToolResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) { return new InoToolResult($"Could not deploy {alias}: no files.", Ok: false); }
        if (Host is not { } host) { return new InoToolResult(Unavailable, Ok: false); }
        var step = ISynapseStudio.StepKey(alias, files);
        if (State.Deployed.TryGetValue(step, out var done)) { return new InoToolResult($"{alias} is already deployed (hash {done})."); }
        DeployOutcome outcome;
        try
        {
            outcome = await host.Deploy(Id.Brain, new SynapseSource(alias, new Dictionary<string, string>(files, StringComparer.Ordinal), null),
                new Dictionary<string, IReadOnlyDictionary<string, string>>(), CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not deploy {alias}: {error.Message}", Ok: false);
        }

        if (outcome.Status is DeployStatus.Failed or DeployStatus.Refused)
        {
            return new InoToolResult($"Could not deploy {alias}: {outcome.Reason ?? outcome.Status.ToString()}"
                + (outcome.Diagnostics.Count == 0 ? "" : "\n" + Describe(outcome.Diagnostics)), Ok: false);
        }

        var deployed = Without(alias);
        deployed[step] = outcome.Hash ?? "";
        await Save(State with { Deployed = deployed }, new SynapseStudioChanged(alias));
        return new InoToolResult($"{alias} is {outcome.Status.ToString().ToLowerInvariant()} (hash {outcome.Hash}).");
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

        await Save(State with { Deployed = Without(alias) }, new SynapseStudioChanged(alias));
        return new InoToolResult($"Retired {alias}.");
    }

    // The host lists only loaded units, and loads a stored one lazily on its alias's first miss. A scheduled unit may see no miss
    // after a restart, so List first restores every stored unit the host has not loaded.
    public async Task<DeployedSource[]> List()
    {
        if (Host is not { } host) { return []; }
        var live = await host.List(Id.Brain, CancellationToken.None);
        if (Context.Services.GetService<ISourceStore>() is { } store && Context.Services.GetService<DigitalBrain.Spi.ISynapseRestorer>() is { } restorer)
        {
            var missing = (await store.List(Id.Brain, CancellationToken.None))
                .Where(record => record.Aliases.Count > 0 && live.All(source => source.SourceKey != record.SourceKey)).ToArray();
            foreach (var record in missing) { await restorer.TryRestore(Id.Brain, record.Aliases[0], CancellationToken.None); }
            if (missing.Length > 0) { live = await host.List(Id.Brain, CancellationToken.None); }
        }

        return [.. live];
    }

    private Dictionary<string, string> Without(string alias)
        => State.Deployed.Where(entry => !entry.Key.StartsWith(alias + ":", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static string Describe(IEnumerable<SynapseDiagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}"));
}
