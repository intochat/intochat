using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Direct contract for starting and inspecting hardened Feature-to-PR runs (<see cref="FeatureToPrFlow"/>).
/// </summary>
[NeuronAlias("ino.f2pr"), DefaultNeuronType("ino.f2pr")]
public interface IFeatureToPr
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "ino.f2pr", "default");

    [Interleaves]
    Task<InoToolResult> ProposeChange(string repository, string goal, CancellationToken ct = default);

    [Interleaves]
    Task<InoToolResult> ProposeChangeOnBranch(string repository, string goal, string baseBranch, CancellationToken ct = default);

    [ReadOnlyCall, Interleaves]
    Task<InoToolResult> ChangeStatus(string flowId, CancellationToken ct = default);

    [ReadOnlyCall, Interleaves]
    Task<InoToolResult> Repositories(CancellationToken ct = default);
}
