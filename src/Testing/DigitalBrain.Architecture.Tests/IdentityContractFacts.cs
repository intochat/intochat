using DigitalBrain.Framework;

namespace DigitalBrain.Architecture.Tests;

// Identity actors are neurons like everything else, so nothing but the platform-only rule keeps
// their Watch/Unwatch and operations off the script edge. Every one of them must carry it.
public sealed class IdentityContractFacts
{
    [Fact]
    public void EveryIdentityNeuronContractIsPlatformOnly()
    {
        var offenders = ProductionArchitecture.Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type) && type != typeof(INeuron)
                && type.Namespace is { } ns && ns.Contains(".Identity", StringComparison.Ordinal))
            .Where(type => !PlatformOnlyAttribute.AppliesTo(type))
            .Select(type => type.FullName!).Order().ToArray();
        Assert.True(offenders.Length == 0, "Identity neuron contracts reachable from scripts:\n" + string.Join("\n", offenders));
    }
}
