using DigitalBrain;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps;

// The build flow's end is the build's end: a failed or cancelled flow fails the running attempt with the flow's detail. A completed
// flow already landed the files through CompleteBuild, so it changes nothing. BeginBuild names the attempt's flow and holds the
// brain-wide FlowDone synapse; a FlowDone of an earlier attempt's flow, or one arriving when the app is no longer Building, is ignored.
internal sealed partial class AppNeuron
{
    [On]
    public Task OnBuildFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (State is not { Status: AppStatus.Building, BuildFlowId: { } flowId } || done.FlowId != flowId
            || done.Outcome is not (FlowOutcome.Failed or FlowOutcome.Cancelled)) { return Task.CompletedTask; }
        return CompleteBuild(State.BuildAttempt, null, done.Detail ?? done.Outcome.ToString());
    }
}
