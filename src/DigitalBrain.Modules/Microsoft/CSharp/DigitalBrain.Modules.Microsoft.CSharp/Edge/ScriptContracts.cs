using System.Reflection;
using DigitalBrain;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Edge;

// The neuron contracts scripts may call: interfaces from the composed modules' contract
// assemblies, nothing else. Composing a module is what exposes its contracts to scripts.
internal sealed class ScriptContracts(ContractCatalog catalog)
{
    private readonly IReadOnlyDictionary<string, Type> _contracts = catalog.VisibleTypes
        .Where(ContractCatalog.IsNeuron)
        // Double filter (assembly + per-type) is deliberate defense-in-depth: credential-bearing contracts protected at both levels.
        .ToDictionary(type => type.FullName!, StringComparer.Ordinal);

    public ScriptContracts(IEnumerable<Assembly> assemblies) : this(new ContractCatalog(assemblies)) { }

    // What a sandbox host must carry to run behaviors compiled here: the vocabulary and every
    // contract assembly scripts may call into.
    public string Fingerprint => _fingerprint ??= ContractsFingerprint.Of([typeof(Signal).Assembly, .. _contracts.Values.Select(type => type.Assembly)]);

    private string? _fingerprint;

    public Type Find(string name) => _contracts.TryGetValue(name, out var contract)
        ? contract
        : throw new ArgumentException($"{name} is not a neuron contract scripts may call.");
}
