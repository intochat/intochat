using DigitalBrain.Flutter.Text;
using DigitalBrain.Revisions;
using DigitalBrain.Revisions.Signals;

// A failure is shown, never retried; the launcher keeps the worktree for inspection.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var index = brain.Get<IRevisions>(SelfImprove.Scope(brain));
await foreach (var changed in brain.On<RevisionChanged>(index, brain.Stopping))
{
    var revision = brain.Get<IRevision>(IRevision.Key(SelfImprove.Scope(brain), changed.Sha));
    await foreach (var failed in brain.On<RevisionFailed>(revision, brain.Stopping))
    {
        var snapshot = await revision.Read();
        var checks = snapshot.Report is null ? "" : "\n\n" + string.Join("\n", snapshot.Report.Checks.Select(check => $"- {(check.Passed ? "ok" : "FAILED")} {check.Name}: {check.Detail}"));
        await brain.Get<IText>(SelfImprove.Key(brain) + "/report").Set($"**{failed.Sha} failed at {failed.Stage}:** {failed.Reason}{checks}");
        await SelfImprove.RenderListAsync(brain);
        break;
    }
}