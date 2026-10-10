using DigitalBrain.Flutter.Text;
using DigitalBrain.Microsoft.Aspire;

// A failure is shown, never retried; the candidate keeps its worktree for inspection.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var index = brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain)));
await foreach (var changed in brain.On<CandidateChanged>(index, brain.Stopping))
{
    var candidate = brain.Get<ICandidate>(ICandidate.Key(SelfImprove.Scope(brain), changed.Sha));
    await foreach (var failed in brain.On<CandidateFailed>(candidate, brain.Stopping))
    {
        var snapshot = await candidate.Read();
        var checks = snapshot.Report is null ? "" : "\n\n" + string.Join("\n", snapshot.Report.Checks.Select(check => $"- {(check.Passed ? "ok" : "FAILED")} {check.Name}: {check.Detail}"));
        await brain.Get<IText>(SelfImprove.Key(brain) + "/report").Set($"**{failed.Sha} failed at {failed.Stage}:** {failed.Reason}{checks}");
        await SelfImprove.RenderListAsync(brain);
        break;
    }
}