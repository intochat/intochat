namespace IntoChat.OS.Modules.Apps;

// The app's own restore turn, plumbing no edge can address (not an INeuron contract). Queued after activation as its own
// turn, so it serializes with edits and never runs inside the first caller's call.
[DigitalBrain.Contracts.NeuronAlias("apps.restore"), DigitalBrain.Contracts.DefaultNeuronType(IntoChat.OS.Connections.AppGrainType.Name)]
internal interface IAppRestore
{
    [DigitalBrain.Contracts.OneWayCall] Task Restore();
}
