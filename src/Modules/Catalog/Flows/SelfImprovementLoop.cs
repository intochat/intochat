using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Table;

namespace IntoChat.OS.Modules.Apps.Flows;

[Neuron("loop.self-improvement")]
internal sealed partial class SelfImprovementLoop : Neuron, ISelfImprovementLoop
{
    [Slot]
    private partial IFlows Flows { get; }

    [Slot(Key = "confirmations")]
    private partial IConfirmationInbox Approval { get; }

    [Slot]
    private partial ITable FindingsLog { get; }

    public async Task<string> Status(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = Flows;
        _ = Approval;
        _ = await FindingsLog.Read();
        return "ready";
    }
}
