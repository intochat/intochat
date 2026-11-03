using DigitalBrain.Capabilities.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Orleans.Concurrency;

namespace IntoChat.OS.Connections.Platform.Connections;

// Internal notification port on the public catalog neuron. One-way avoids a cycle when a
// catalog read is already awaiting the connection that has just changed.
// A plain contract of the brain's connection requests neuron "{brain}/main" (06-08).
[DigitalBrain.Contracts.NeuronAlias("integrations.catalog-updates"), DigitalBrain.Contracts.DefaultNeuronType(TypeName)]
[DigitalBrain.Access.Internal]
public interface IConnectionCatalogUpdates
{
    const string TypeName = "integrations.accounts";
    static DigitalBrain.NeuronId Id(string brainId) => new(new DigitalBrain.BrainId(brainId), TypeName, "main");

    [DigitalBrain.OneWayCall] Task Changed(string connectionKey);
}
