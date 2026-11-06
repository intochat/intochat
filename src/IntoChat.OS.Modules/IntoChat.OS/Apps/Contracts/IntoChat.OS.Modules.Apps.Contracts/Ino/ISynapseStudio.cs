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

    static string StepKey(string alias, IReadOnlyDictionary<string, string> files)
    {
        var text = string.Concat(files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => $"{file.Key}\0{file.Value}\0"));
        return $"{alias}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";
    }

    // Compiles the unit with the synapse analyzers and returns their diagnostics.
    Task<InoToolResult> Check(string source);

    // Deploys a one-file unit; the text names the build hash.
    Task<InoToolResult> Deploy(string alias, string source);

    // Deploys a unit of several files, keyed by path.
    Task<InoToolResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files);

    Task<InoToolResult> Retire(string alias);

    [DigitalBrain.ReadOnlyCall]
    Task<DigitalBrain.DeployedSource[]> List();
}

// The step keys deployed so far, each with the build hash it produced.
public sealed record SynapseStudioState(IReadOnlyDictionary<string, string> Deployed)
{
    public SynapseStudioState() : this(new Dictionary<string, string>(StringComparer.Ordinal)) { }
}

[DigitalBrain.Signal("ino.synapses.changed")]
public sealed record SynapseStudioChanged(string Alias) : DigitalBrain.Signal;
