using DigitalBrain.Revisions;
using DigitalBrain.Revisions.Signals;

// A verified revision is promoted. The launcher then stops this brain; the child finds
// RevisionPromoted in its own copied history.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var index = brain.Get<IRevisions>(SelfImprove.Scope(brain));
await foreach (var changed in brain.On<RevisionChanged>(index, brain.Stopping))
{
    var revision = brain.Get<IRevision>(IRevision.Key(SelfImprove.Scope(brain), changed.Sha));
    await foreach (var verified in brain.On<RevisionVerified>(revision, brain.Stopping))
    {
        if ((await revision.Read()).Stage == RevisionStage.Verified) { await revision.Promote(); }
        await SelfImprove.RenderListAsync(brain);
        break;
    }
}