using DigitalBrain.Microsoft.Aspire;

// A healthy child is verified with the plan it was proposed with. Verify is idempotent on stage:
// a replayed signal after a reap finds the candidate past Healthy and is refused harmlessly.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var index = brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain)));
await foreach (var changed in brain.On<CandidateChanged>(index, brain.Stopping))
{
    var candidate = brain.Get<ICandidate>(ICandidate.Key(SelfImprove.Scope(brain), changed.Sha));
    await foreach (var healthy in brain.On<CandidateHealthy>(candidate, brain.Stopping))
    {
        var snapshot = await candidate.Read();
        if (snapshot.Stage != CandidateStage.Healthy || snapshot.Plan is null) { break; }
        await candidate.Verify();
        await SelfImprove.RenderListAsync(brain);
        break;
    }
}