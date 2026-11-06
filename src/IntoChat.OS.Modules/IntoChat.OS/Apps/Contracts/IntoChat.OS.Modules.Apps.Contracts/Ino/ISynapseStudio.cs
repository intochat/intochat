using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Ino's tool over the brain's synapse host: check, deploy, retire and list compiled synapse units. A unit's alias is its source key.
[NeuronAlias("ino.synapses"), DefaultNeuronType("ino.synapses")]
public interface ISynapseStudio
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "ino.synapses", "ino/synapses");

    // The idempotency key of a task step that deploys source as alias: the same source deploys once.
    static string StepKey(string alias, string source)
        => $"{alias}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}";

    // Compiles the unit with the synapse analyzers and returns their diagnostics.
    Task<InoToolResult> Check(string source);

    // Deploys the unit; the text names the build hash.
    Task<InoToolResult> Deploy(string alias, string source);

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
