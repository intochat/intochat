using IntoChat.OS.Microsoft.Aspire;

// A verified candidate is promoted: the cluster restarts on its build and the new silos find
// CandidatePromoted in the candidate's history. Each candidate is followed only until it is past Verified,
// so one that fails cannot hold up the next.
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
    // Configuration restoration has no candidate signal: retain this step and reconcile until it returns.
    while (!await SelfImprove.IsAvailableAsync(brain))
    {
        await SelfImprove.RenderListAsync(brain);
        await Task.Delay(TimeSpan.FromSeconds(2), brain.Stopping);
    }
    var snapshot = await candidate.Read();
    if (snapshot.Stage == CandidateStage.Verified)
    {
        snapshot = await candidate.Promote();
        await SelfImprove.RenderListAsync(brain);
    }
    return snapshot.Stage > CandidateStage.Verified;
}
