using DigitalBrain.Microsoft.Aspire;

// A verified candidate is promoted: the cluster restarts on its build and the new silos find
// CandidatePromoted in the candidate's history. Each candidate is followed only until it is past Verified,
// so one that fails cannot hold up the next.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
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
    if (snapshot.Stage == CandidateStage.Verified)
    {
        snapshot = await candidate.Promote();
        await SelfImprove.RenderListAsync(brain);
    }
    return snapshot.Stage > CandidateStage.Verified;
}
