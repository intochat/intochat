namespace IntoChat.OS.Modules.Apps.Flows;

// The flows index changed its entry for FlowId.
[DigitalBrain.Signal("flows.changed")]
public sealed record FlowsChanged(string FlowId) : DigitalBrain.Signal;
