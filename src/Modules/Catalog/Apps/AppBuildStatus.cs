using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps;

// An app's build status as the person sees it: while the app is Building, its build flow's status (IFlows.Read of IApp.BuildFlow)
// decides. A failed or cancelled flow shows the app Failed with the flow's detail; a flow the index does not know, or cannot read,
// leaves the stored status. Read by the app's readers, not by AppNeuron: the flow itself calls the app.
internal static class AppBuildStatus
{
    public static async Task<AppSnapshot> Of(AppSnapshot snapshot, IApp app, IFlows flows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != AppStatus.Building) { return snapshot; }
        FlowView? view;
        try { view = await app.BuildFlow().WaitAsync(ct) is { } flowId ? await flows.Read(flowId, ct) : null; }
        catch (Exception error) when (error is not OperationCanceledException) { return snapshot; }
        return view?.Status is FlowStatus.Failed or FlowStatus.Cancelled
            ? snapshot with { Status = AppStatus.Failed, Activity = [.. snapshot.Activity, "build failed: " + (view.Detail ?? view.Status.ToString())] }
            : snapshot;
    }
}
