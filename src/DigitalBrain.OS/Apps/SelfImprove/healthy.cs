using DigitalBrain.Revisions;
using DigitalBrain.Revisions.Signals;

// A healthy child is verified with the plan it was proposed with. Verify is idempotent on stage:
// a replayed signal after a reap finds the revision past Healthy and is refused harmlessly.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var index = brain.Get<IRevisions>(SelfImprove.Scope(brain));
await foreach (var changed in brain.On<RevisionChanged>(index, brain.Stopping))
{
    var revision = brain.Get<IRevision>(IRevision.Key(SelfImprove.Scope(brain), changed.Sha));
    await foreach (var healthy in brain.On<RevisionHealthy>(revision, brain.Stopping))
    {
        var snapshot = await revision.Read();
        if (snapshot.Stage != RevisionStage.Healthy || snapshot.Plan is null) { break; }
        await revision.Verify(snapshot.Plan);
        await SelfImprove.RenderListAsync(brain);
        break;
    }
}