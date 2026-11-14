using DigitalBrain.Brains;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Dedicated neuron forwarding <see cref="IFeatureToPr"/> operations to <see cref="IInoStudio"/> in the same brain.
/// </summary>
[DigitalBrain.Neurons.Neuron("ino.f2pr")]
public sealed class FeatureToPrNeuron : DigitalBrain.Neurons.Neuron, IFeatureToPr
{
    private IInoStudio Studio
    {
        get
        {
            var brain = Id.Brain.Value;
            if (string.IsNullOrWhiteSpace(brain) || !BrainScope.IsValidId(brain))
            {
                throw new InvalidOperationException($"Invalid brain id '{brain}'.");
            }
            return Get<IInoStudio>(new DigitalBrain.NeuronId(Id.Brain, "ino.studio", "studio"));
        }
    }

    public Task<InoToolResult> ProposeChange(string repository, string goal, CancellationToken ct = default)
        => Studio.ProposeChange(repository, goal, ct);

    public Task<InoToolResult> ProposeChangeOnBranch(string repository, string goal, string baseBranch, CancellationToken ct = default)
        => Studio.ProposeChangeOnBranch(repository, goal, baseBranch, ct);

    public Task<InoToolResult> ChangeStatus(string flowId, CancellationToken ct = default)
        => Studio.ChangeStatus(flowId, ct);

    public Task<InoToolResult> Repositories(CancellationToken ct = default)
        => Studio.Repositories(ct);
}
