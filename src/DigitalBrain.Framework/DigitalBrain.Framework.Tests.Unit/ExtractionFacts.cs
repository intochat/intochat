using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class ExtractionFacts
{
    [Fact]
    public void TheDigitalBrainAssemblyExportsOnlyTheWords()
    {
        var exported = typeof(INeuron).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "DigitalBrain" && !type.Name.Contains('_', StringComparison.Ordinal))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("DigitalBrain", typeof(INeuron).Assembly.GetName().Name);
        Assert.Equal(
            ["Activated", "Created", "IDigitalBrain", "IKernel", "INeuron", "INeuronObserver", "IRetainsSignals", "NeuronId", "Owner", "RetainedSignal", "ServiceUnavailableException", "Signal", "SignalId", "State", "Synapse"],
            exported);
    }

    [Fact]
    public void AnEmptyCompositionStillExposesTheWordsAndTheKernelContracts()
    {
        var assemblies = new ModuleInventory([]).ContractAssemblies().Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["DigitalBrain", "DigitalBrain.Platform.Contracts"], assemblies);
        Assert.DoesNotContain(new ModuleInventory([]).ContractAssemblies(), PlatformAssemblyAttribute.IsPlatform);
    }
}
