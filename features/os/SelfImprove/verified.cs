using DigitalBrain.Microsoft.Aspire;

// A verified candidate is promoted: the cluster restarts on its build and the new silos find
// CandidatePromoted in the candidate's history.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var index = brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain)));
await foreach (var changed in brain.On<CandidateChanged>(index, brain.Stopping))
{
    var candidate = brain.Get<ICandidate>(ICandidate.Key(SelfImprove.Scope(brain), changed.Sha));
    await foreach (var verified in brain.On<CandidateVerified>(candidate, brain.Stopping))
    {
        if ((await candidate.Read()).Stage == CandidateStage.Verified) { await candidate.Promote(); }
        await SelfImprove.RenderListAsync(brain);
        break;
    }
}