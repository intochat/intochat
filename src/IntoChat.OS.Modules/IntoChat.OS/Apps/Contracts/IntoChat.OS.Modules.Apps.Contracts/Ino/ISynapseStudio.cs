using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Ino's tool over the brain's synapse host: check, deploy, retire and list compiled synapse units. A unit's alias is its source key.
[NeuronAlias("ino.synapses"), DefaultNeuronType("ino.synapses")]
public interface ISynapseStudio
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "ino.synapses", "ino/synapses");

    // The task step capability the executor runs through Deploy: IdempotencyKey = StepKey(alias, source), Description = source.
    const string DeployCapability = "synapse.deploy";

    // The single file Deploy(alias, source) deploys.
    static IReadOnlyDictionary<string, string> SingleFile(string alias, string source) => new Dictionary<string, string>(StringComparer.Ordinal) { [$"{alias}.cs"] = source };

    // The idempotency key of a step that deploys source as alias: the same source deploys once.
    static string StepKey(string alias, string source) => StepKey(alias, SingleFile(alias, source));

    // Bindings are part of the key: the same files bound to another connection deploy again.
    static string StepKey(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string>? bindings = null)
    {
        var text = string.Concat(files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"{file.Key}\0{file.Value}\0"))
            + string.Concat((bindings ?? new Dictionary<string, string>()).OrderBy(slot => slot.Key, StringComparer.Ordinal).Select(slot => $"@{slot.Key}\0{slot.Value}\0"));
        return $"{alias}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";
    }

    // Compiles the unit with the synapse analyzers and returns their diagnostics.
    Task<InoToolResult> Check(string source);

    // Deploys a one-file unit; the text names the build hash.
    Task<InoToolResult> Deploy(string alias, string source);

    // Deploys a unit of several files, keyed by path, with its slot bindings (slot name -> connection key) for the state alias that
    // is the unit's alias. The result carries the host's install outcome per state alias: a new or changed need waits as Pending.
    Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings);

    Task<InoToolResult> Retire(string alias);

    [DigitalBrain.ReadOnlyCall]
    Task<DigitalBrain.DeployedSource[]> List();

    // The non-.cs files (spec.md, scenarios.json) the last deploy of alias carried; the host compiles every file it is given, so the
    // studio keeps them beside the source. Empty for an alias never deployed or retired.
    [DigitalBrain.ReadOnlyCall]
    Task<Dictionary<string, string>> Sidecars(string alias);
}

// One DeployFiles outcome: Ok with the host's status and build hash, else Text is why. Installs is the host's install outcome per
// state alias (DigitalBrain.InstallOutcome.Reason is the confirmation plan id while Pending).
public sealed record UnitDeployResult(bool Ok, string Text, DigitalBrain.DeployStatus Status, string? Hash,
    IReadOnlyDictionary<string, DigitalBrain.InstallOutcome> Installs);

// The step keys deployed so far, each with the build hash it produced.
// Sidecars[alias][file] are the non-.cs files of alias's last deploy (added field; null in state saved before it).
public sealed record SynapseStudioState(IReadOnlyDictionary<string, string> Deployed)
{
    public SynapseStudioState() : this(new Dictionary<string, string>(StringComparer.Ordinal)) { }

    public Dictionary<string, Dictionary<string, string>>? Sidecars { get; init; }
}

[DigitalBrain.Signal("ino.synapses.changed")]
public sealed record SynapseStudioChanged(string Alias) : DigitalBrain.Signal;
