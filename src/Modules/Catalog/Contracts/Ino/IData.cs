using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record InoDataState(string? ConnectedDatabase, InoCard[] Charts)
{
    public InoDataState() : this(null, []) { }
}

[DigitalBrain.Signal("ino.data.changed")]
public sealed record InoDataChanged(long Revision) : DigitalBrain.Signal;

[NeuronAlias("data", Formerly = ["ino.data", "IntoChat.OS.Modules.Apps.IInoData"]), DefaultNeuronType("ino.data")]
public interface IData
{
    const string DefaultKey = "ino/data";

    Task<InoToolResult> Connect(string connection);

    Task<InoToolResult> Query(string sql);

    Task<InoToolResult> Chart(string spec);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoDataState> Read();
}
