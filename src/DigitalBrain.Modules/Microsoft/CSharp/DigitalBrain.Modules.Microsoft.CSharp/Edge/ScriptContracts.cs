using System.Reflection;
using DigitalBrain;
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

    public Type Find(string name) => _contracts.TryGetValue(name, out var contract)
        ? contract
        : throw new ArgumentException($"{name} is not a neuron contract scripts may call.");
}
