using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Flows;

[NeuronAlias("loop.self-improvement"), DefaultNeuronType("loop.self-improvement")]
[Description("Autonomous self-improvement loop component with declared [Slot] dependencies.")]
public interface ISelfImprovementLoop
{
    const string DefaultKey = "default";

    [ReadOnlyCall, Interleaves]
    Task<string> Status(CancellationToken ct = default);
}
