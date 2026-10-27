using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Microsoft.Aspire;

// A failure is shown, never retried; the candidate keeps its worktree for inspection. Each candidate is
// followed only until it ends, so one that succeeds cannot hold up the next.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var index = brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain)));
await foreach (var changed in brain.On<CandidateChanged>(index, brain.Stopping))
{
    var candidate = brain.Get<ICandidate>(ICandidate.Key(SelfImprove.Scope(brain), changed.Sha));
    if (await Step(candidate)) { continue; }
    await foreach (var stage in brain.On<CandidateChanged>(candidate, brain.Stopping))
    {
        if (await Step(candidate)) { break; }
    }
}

async Task<bool> Step(ICandidate candidate)
{
    var snapshot = await candidate.Read();
    if (snapshot.Stage == CandidateStage.Failed)
    {
        var checks = snapshot.Report is null ? "" : "\n\n" + string.Join("\n", snapshot.Report.Checks.Select(check => $"- {(check.Passed ? "ok" : "FAILED")} {check.Name}: {check.Detail}"));
        await brain.Get<IText>(SelfImprove.Key(brain) + "/report").Set($"**{snapshot.Sha} failed at {snapshot.History[^2]}:** {snapshot.Failure}{checks}");
        await SelfImprove.RenderListAsync(brain);
    }
    return SelfImprove.Ended(snapshot.Stage);
}
