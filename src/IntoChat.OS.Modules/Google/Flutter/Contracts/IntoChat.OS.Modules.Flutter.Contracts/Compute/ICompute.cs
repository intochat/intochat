using DigitalBrain.Contracts;


namespace IntoChat.OS.Modules.Flutter.Compute;

[NeuronAlias("compute"), DefaultNeuronType("ui.compute")]
public interface ICompute
{
    [DigitalBrain.ReadOnlyCall] Task<ComputeState> Read();
}

[GenerateSerializer, Alias("ui.compute-state")]
public sealed record ComputeState(
    [property: Id(0)] long SpentMicros,
    [property: Id(1)] DateTimeOffset Since,
    [property: Id(2)] ComputeRow[] Rows);

[GenerateSerializer, Alias("ui.compute-row")]
public sealed record ComputeRow(
    [property: Id(0)] string Key,
    [property: Id(1)] long Micros,
    [property: Id(2)] long Count);
