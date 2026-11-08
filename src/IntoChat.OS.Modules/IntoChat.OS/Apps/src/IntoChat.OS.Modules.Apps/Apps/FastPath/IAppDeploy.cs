using DigitalBrain.Capabilities;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Modules.Apps;

// The app's own restore turn, plumbing no edge can address (not an INeuron contract).
// A module-internal plain contract of the app neuron (06-08), alias unchanged.
[DigitalBrain.Contracts.NeuronAlias("apps.deploy"), DigitalBrain.Contracts.DefaultNeuronType(IntoChat.OS.Connections.AppGrainType.Name)]
internal interface IAppDeploy
{
    // Queued after activation as its own turn, so it serializes with edits.
    [DigitalBrain.OneWayCall] Task Restore();
}
