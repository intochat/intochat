namespace DigitalBrain.Kernel;

/// <summary>
/// Marks kernel code a neuron's turn runs through (subscribing, delivery, reaction dispatch). Its awaits stay on the turn:
/// DB0050 refuses <c>ConfigureAwait(false)</c> here as it does inside a neuron.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class TurnBoundAttribute : Attribute;
