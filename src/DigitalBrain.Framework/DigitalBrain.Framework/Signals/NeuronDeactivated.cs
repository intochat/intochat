namespace DigitalBrain.Framework.Signals;

[GenerateSerializer, Alias("brain.neuron-deactivated")]
public sealed record NeuronDeactivated : NeuronActivity;
