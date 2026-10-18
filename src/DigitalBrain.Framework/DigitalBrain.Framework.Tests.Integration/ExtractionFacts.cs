using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Framework.Tests.Integration;

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
            ["AccessAttribute", "Activated", "CallTimeoutAttribute", "Created", "DefaultNeuronTypeAttribute", "FieldAttribute", "IBrain", "IDigitalBrain", "IKernelView", "ILogic`1", "INeuron", "INeuronObserver", "IRetainsSignals", "ISelectiveWatch", "Instance", "InstanceCreated", "InstanceRetired", "InterleavesAttribute", "InterleavesWhenAttribute", "InternalAttribute", "JournalConflictException", "LeaseLostException", "NeuronAliasAttribute", "NeuronAliases", "NeuronContractAttribute", "NeuronId", "OneWayCallAttribute", "Owner", "PerAccountAttribute", "PublicAttribute", "ReadOnlyCallAttribute", "RetainedSignal", "RevisionConflictException", "RollbackDetectedException", "ServiceUnavailableException", "SharedAttribute", "Signal", "SignalId", "SignalNudge", "SignalPage", "Sources", "State", "Synapse", "Trace", "TypeAdded", "TypeEntry", "TypeKind", "Verdict"],
            exported);
    }

    [Fact]
    public void AnEmptyCompositionStillExposesTheWordsAndTheKernelContracts()
    {
        var assemblies = new ModuleInventory([]).ContractAssemblies().Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["DigitalBrain", "DigitalBrain.Authority.Contracts"], assemblies);
    }
}
