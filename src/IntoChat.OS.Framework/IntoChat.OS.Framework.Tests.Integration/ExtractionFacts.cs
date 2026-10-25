using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Contracts;

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
            ["Activated", "CallTimeoutAttribute", "Created", "IBrain", "IInitialSignalWait", "IIntoChatOS", "IKernelView", "ILogic`1", "INeuron", "INeuronObserver", "IRetainsSignals", "ISelectiveWatch", "ISignalDelivery", "Instance", "InstanceCreated", "InstanceRetired", "InterleavesAttribute", "InterleavesWhenAttribute", "JournalConflictException", "LeaseLostException", "NeuronAliases", "NeuronId", "OneWayCallAttribute", "Owner", "PlainNeuronClient", "ReadOnlyCallAttribute", "RetainedSignal", "RevisionConflictException", "RollbackDetectedException", "ServiceUnavailableException", "Signal", "SignalDeliveries", "SignalId", "SignalNudge", "SignalPage", "Sources", "StaleActionException", "State", "Synapse", "Trace", "TypeAdded", "TypeEntry", "TypeKind", "Verdict", "WatchStream`1"],
            exported);
    }

    [Fact]
    public void AnEmptyCompositionStillExposesTheWordsAndTheKernelContracts()
    {
        var assemblies = new ModuleInventory([]).ContractAssemblies().Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["IntoChat.OS", "IntoChat.OS.Authority.Contracts"], assemblies);
    }
}
