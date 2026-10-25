namespace DigitalBrain.Runtime.Orleans.Synapses;

/// <summary>
/// Test-only seam (04-15 kill-between fact): registered as a singleton, no-op by default. When <see cref="FailAfterAppend"/>
/// is set, <see cref="SignalPublisher.Publish"/> throws after the journal append and before any stream item is sent.
/// </summary>
internal sealed class PublishFaults
{
    public bool FailAfterAppend { get; set; }
}
