namespace IntoChat.OS.Modules.Apps.Signals;

// A request from outside the app: what the app's synapses react to, and the id they write their
// answer under. Published only, never stored: the brain runtime buffers it for each subscriber.
[DigitalBrain.Signal("apps.app-requested")]
public sealed record AppRequested(string Operation, string Input, Guid RequestId) : DigitalBrain.Signal;
