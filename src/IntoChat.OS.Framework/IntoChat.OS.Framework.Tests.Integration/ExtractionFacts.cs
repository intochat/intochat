using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class ExtractionFacts
{
    [Fact]
    public void TheIntoChatOSAssemblyExportsOnlyTheWords()
    {
        var exported = typeof(INeuron).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "IntoChat.OS" && !type.Name.Contains('_', StringComparison.Ordinal))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("IntoChat.OS", typeof(INeuron).Assembly.GetName().Name);
        Assert.Equal(
            ["AccessAttribute", "Activated", "CallTimeoutAttribute", "Created", "DefaultNeuronTypeAttribute", "ExportableStateAttribute", "FieldAttribute", "IBrain", "IInitialSignalWait", "IIntoChatOS", "IKernelView", "ILogic`1", "INeuron", "INeuronObserver", "IRetainsSignals", "ISelectiveWatch", "ISignalDelivery", "Instance", "InstanceCreated", "InstanceRetired", "InterleavesAttribute", "InterleavesWhenAttribute", "InternalAttribute", "JournalConflictException", "LeaseLostException", "NeuronAliasAttribute", "NeuronContractAttribute", "NeuronId", "OneWayCallAttribute", "Owner", "PerAccountAttribute", "PublicAttribute", "ReadOnlyCallAttribute", "RetainedSignal", "RevisionConflictException", "RollbackDetectedException", "ServiceUnavailableException", "SharedAttribute", "Signal", "SignalDeliveries", "SignalId", "SignalNudge", "SignalPage", "Sources", "StaleActionException", "State", "Synapse", "Trace", "TypeAdded", "TypeEntry", "TypeKind", "Verdict", "WatchStream`1"],
            exported);
    }

    [Fact]
    public void AnEmptyCompositionStillExposesTheWordsAndTheKernelContracts()
    {
        var assemblies = new ModuleInventory([]).ContractAssemblies().Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["IntoChat.OS", "IntoChat.OS.Authority.Contracts"], assemblies);
    }
}
