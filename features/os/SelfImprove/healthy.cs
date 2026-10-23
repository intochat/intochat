using IntoChat.OS.Microsoft.Aspire;

// A healthy child is verified with the plan it was proposed with. Each candidate is followed only until it
// is past Healthy, so one that never gets healthy cannot hold up the next. Verify is idempotent on stage:
// a replay after a reap finds the candidate past Healthy and moves on.
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
    if (!await SelfImprove.IsAvailableAsync(brain))
    {
        await SelfImprove.RenderListAsync(brain);
        return true;
    }
    var snapshot = await candidate.Read();
    if (snapshot.Stage == CandidateStage.Healthy && snapshot.Plan is not null)
    {
        snapshot = await candidate.Verify();
        await SelfImprove.RenderListAsync(brain);
    }
    return snapshot.Stage > CandidateStage.Healthy;
}
