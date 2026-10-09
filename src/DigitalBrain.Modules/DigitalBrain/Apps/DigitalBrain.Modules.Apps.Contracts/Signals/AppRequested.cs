namespace DigitalBrain.Apps.Signals;

// A request from outside the app: what the app's synapses react to, and the id they write their
// answer under. Published only, never stored: the brain runtime buffers it for each subscriber.
[GenerateSerializer, Alias("apps.app-requested")]
public sealed record AppRequested([property: Id(0)] string Operation, [property: Id(1)] string Input, [property: Id(2)] Guid RequestId) : Signal;
