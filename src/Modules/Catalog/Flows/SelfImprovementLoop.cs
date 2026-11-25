using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps.Flows;

[Neuron("loop.self-improvement")]
internal sealed partial class SelfImprovementLoop : Neuron, ISelfImprovementLoop
{
    [Slot]
    private partial IFlows Flows { get; }

    [Slot(Key = "confirmations")]
    private partial IConfirmationInbox Approval { get; }

    // Where the loop keeps its findings: a storage connection the installer binds; it has no default.
    [Slot]
    private partial IStorage FindingsLog { get; }

    public Task<string> Status(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = Flows;
        _ = Approval;
        _ = FindingsLog;
        return Task.FromResult("ready");
    }
}
