using IntoChat.OS;
using IntoChat.OS.AI.Agents.Intents.Signals;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.AI.Agents.Intents;

[GenerateSerializer, Alias("intents.state")]
public sealed record IntentsState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public Intent[] Intents { get; init; } = [];
}

[GrainType("intents")]
internal sealed class IntentsNeuron(
    [PersistentState("intents", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<IntentsState> store)
    : Neuron<IntentsState>(store), IIntents
{
    public async Task<Guid> Declare(IntentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.RequestId) || draft.RequestId.Length > 200) { throw new ArgumentException("An intent needs a request id of at most 200 characters.", nameof(draft)); }
        if (string.IsNullOrWhiteSpace(draft.Heard) || draft.Heard.Length > 8000) { throw new ArgumentException("An intent needs heard text of at most 8000 characters.", nameof(draft)); }
        var known = Array.Find(Snapshot.Intents, item => item.RequestId == draft.RequestId);
        if (known is not null) { return known.Id; }
        if (draft.Continues is { } continues)
        {
            if (Array.Exists(Snapshot.Intents, item => item.Id == continues)) { return continues; }
            throw new ArgumentException("The intent to continue does not exist.", nameof(draft));
        }
        var active = Snapshot.Intents.Count(item => item.State is not (IntentState.Done or IntentState.Abandoned));
        if (active >= IIntents.ActiveCap) { throw new InvalidOperationException($"At most {IIntents.ActiveCap} intents can be active; finish or abandon one first."); }
        var id = Guid.NewGuid();
        var intent = new Intent(id, draft.Heard, draft.Conversation ?? "", draft.RequestId, null, 0, IntentState.Heard, [], [],
            [new IntentTransition(IntentState.Heard, DateTimeOffset.UtcNow, draft.RequestId, "heard")]);
        var revision = Snapshot.Revision + 1;
        await Save(Snapshot with { Revision = revision, Intents = [.. Snapshot.Intents, intent] }, new IntentHeard(this.GetPrimaryKeyString(), id, draft.RequestId));
        return id;
    }

    public Task Rephrase(Guid id, IntentBrief brief, double confidence, long expectedRevision) => throw new NotImplementedException();

    public Task Transition(Guid id, IntentState to, string reason, long expectedRevision) => throw new NotImplementedException();

    public Task Arm(Guid id, Cue[] cues, long expectedRevision) => throw new NotImplementedException();

    public Task<IntentsSnapshot> Read() => Task.FromResult(new IntentsSnapshot(Snapshot.Revision, Snapshot.Intents.ToArray()));
}
