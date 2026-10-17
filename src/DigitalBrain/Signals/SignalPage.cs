namespace DigitalBrain;

/// <summary>
/// One page of a neuron's journalled signals, returned by <see cref="INeuron.Read"/>.
/// </summary>
/// <remarks>
/// <see cref="Sequences"/>[i] is the journal sequence of <see cref="Signals"/>[i]. <see cref="Floor"/>
/// is the newest sequence the journal has dropped (0 when nothing was dropped): a reader whose
/// watermark is below it has missed signals. <see cref="Last"/> is the newest sequence in the
/// publisher's brain; a reader is caught up when its watermark reaches it.
/// </remarks>
[GenerateSerializer, Alias("brain.signal-page")]
public sealed record SignalPage([property: Id(0)] long Floor, [property: Id(1)] long Last, [property: Id(2)] Signal[] Signals, [property: Id(3)] long[] Sequences);
