using DigitalBrain.Contracts;


namespace IntoChat.OS.Modules.Flutter.Compute;

[NeuronAlias("compute"), DefaultNeuronType("ui.compute")]
public interface ICompute
{
    [DigitalBrain.ReadOnlyCall] Task<ComputeState> Read();
}

public sealed record ComputeState(
    long SpentMicros,
    DateTimeOffset Since,
    ComputeRow[] Rows);

public sealed record ComputeRow(
    string Key,
    long Micros,
    long Count);
