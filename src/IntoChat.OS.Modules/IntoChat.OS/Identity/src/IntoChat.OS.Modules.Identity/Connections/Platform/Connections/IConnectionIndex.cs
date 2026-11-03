using DigitalBrain.Capabilities.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// One per brain: the keys of its connection neurons, nothing else.
// A plain neuron (06-08), the brain's singleton "{brain}/main" (R4-19). [Internal]: module code reaches it, no edge caller does,
// and the script contract catalog leaves it out, as it did when it was no neuron.
[DigitalBrain.Contracts.NeuronAlias("connection.index"), DigitalBrain.Contracts.DefaultNeuronType(TypeName)]
[DigitalBrain.Access.Internal]
public interface IConnectionIndex
{
    const string TypeName = "connection.index";
    static string Key(string brainId) => $"{brainId}/main";
    static DigitalBrain.NeuronId Id(string brainId) => new(new DigitalBrain.BrainId(brainId), TypeName, "main");

    Task<string[]> List();
    Task Track(string connectionKey);
    Task Untrack(string connectionKey);
}
