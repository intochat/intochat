using IntoChat.OS.Modules.Microsoft.Aspire;

// The candidates list follows every stage: rebuilt from scratch when this synapse starts and whenever a
// candidate moves. The set is every candidate the kernel knows, re-selected when the index proposes or
// ends one; ended candidates stay in it so their last move is never missed. The index itself is not a
// member: it is already watched as the registry, and request renders the list once Propose answers.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var candidates = Sources.Named<ICandidates, CandidateChanged, ICandidate>(brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain))), async (_, ct) =>
{
    if (!await SelfImprove.IsAvailableAsync(brain)) { return []; }
    var members = new List<ICandidate>();
    await foreach (var instance in brain.Kernel.Instances<ICandidate>(ct)) { members.Add(brain.Get<ICandidate>(instance.Key)); }
    return [.. members];
});
await brain.React<CandidateChanged>(candidates, _ => SelfImprove.RenderListAsync(brain), brain.Stopping);
