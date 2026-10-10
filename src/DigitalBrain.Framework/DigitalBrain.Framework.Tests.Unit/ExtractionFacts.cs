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
            ["AccessAttribute", "Activated", "Created", "IBrain", "IDigitalBrain", "IKernelView", "INeuron", "INeuronObserver", "IRetainsSignals", "Instance", "InstanceCreated", "InstanceRetired", "InternalAttribute", "NeuronId", "Owner", "PerAccountAttribute", "PublicAttribute", "RetainedSignal", "ServiceUnavailableException", "SharedAttribute", "Signal", "SignalId", "State", "Synapse", "Trace", "TypeAdded", "TypeEntry", "TypeKind"],
            exported);
    }

    [Fact]
    public void AnEmptyCompositionStillExposesTheWordsAndTheKernelContracts()
    {
        var assemblies = new ModuleInventory([]).ContractAssemblies().Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["DigitalBrain", "DigitalBrain.Platform.Contracts"], assemblies);
    }
}
