namespace IntoChat.OS.Modules.Apps;

// Module-internal contract of the app neuron used to queue post-activation deployment restoration.
[DigitalBrain.Contracts.NeuronAlias("apps.deploy"), DigitalBrain.Contracts.DefaultNeuronType(IntoChat.OS.Connections.AppGrainType.Name)]
internal interface IAppDeploy
{
    // Queued after activation as its own turn, so it serializes with edits.
    [DigitalBrain.OneWayCall] Task Restore();
}