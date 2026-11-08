using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record InoDataState(string? ConnectedDatabase, InoCard[] Charts)
{
    public InoDataState() : this(null, []) { }
}

[DigitalBrain.Signal("ino.data.changed")]
public sealed record InoDataChanged(long Revision) : DigitalBrain.Signal;

[NeuronAlias("ino.data"), DefaultNeuronType("ino.data")]
public interface IInoData
{
    Task<InoToolResult> Connect(string connection);

    Task<InoToolResult> Query(string sql);

    Task<InoToolResult> Chart(string spec);

    [DigitalBrain.ReadOnlyCall]
    Task<InoDataState> Read();
}
