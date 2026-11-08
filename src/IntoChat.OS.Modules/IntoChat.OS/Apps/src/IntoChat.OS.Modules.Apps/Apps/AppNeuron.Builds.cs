using DigitalBrain;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps;

// The build flow's end is the build's end: a failed or cancelled flow fails the running attempt with the flow's detail. A completed
// flow already landed the files through CompleteBuild, so it changes nothing. BuildFlowStarted holds the brain-wide FlowDone synapse.
internal sealed partial class AppNeuron
{
    [On]
    public Task OnBuildFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (State.BuildFlowId is not { } flowId || done.FlowId != flowId || done.Outcome == FlowOutcome.Completed) { return Task.CompletedTask; }
        return CompleteBuild(State.BuildAttempt, null, done.Detail ?? done.Outcome.ToString());
    }
}
