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

    public async Task Rephrase(Guid id, IntentBrief brief, double confidence, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var intent = Find(id, expectedRevision);
        if (intent.State is IntentState.Done or IntentState.Abandoned) { throw new InvalidOperationException($"An intent in state {intent.State} cannot be rephrased."); }
        var keeps = intent.State is IntentState.Dispatched or IntentState.Waiting;
        var next = intent with
        {
            Brief = brief,
            Confidence = confidence,
            State = keeps ? intent.State : IntentState.Rephrased,
            History = keeps ? intent.History : [.. intent.History, new IntentTransition(IntentState.Rephrased, DateTimeOffset.UtcNow, intent.RequestId, "rephrased")],
        };
        await Save(Replace(next), new IntentRephrased(this.GetPrimaryKeyString(), id, intent.RequestId, confidence));
    }

    public async Task Transition(Guid id, IntentState to, string reason, long expectedRevision)
    {
        var intent = Find(id, expectedRevision);
        var legal = (intent.State, to) switch
        {
            (IntentState.Rephrased, IntentState.Confirmed) => true,
            (IntentState.Confirmed, IntentState.Dispatched) => true,
            (IntentState.Dispatched, IntentState.Waiting) => true,
            (IntentState.Waiting, IntentState.Dispatched) => true,
            (IntentState.Dispatched or IntentState.Waiting, IntentState.Done) => true,
            (IntentState.Heard or IntentState.Rephrased or IntentState.Confirmed or IntentState.Dispatched or IntentState.Waiting, IntentState.Abandoned) => true,
            _ => false,
        };
        if (!legal) { throw new InvalidOperationException($"An intent cannot move from {intent.State} to {to}."); }
        var next = intent with
        {
            State = to,
            History = [.. intent.History, new IntentTransition(to, DateTimeOffset.UtcNow, intent.RequestId, reason ?? "")],
        };
        await Save(Replace(next), new IntentTransitioned(this.GetPrimaryKeyString(), id, intent.State, to));
    }

    public async Task Arm(Guid id, Cue[] cues, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var intent = Find(id, expectedRevision);
        await Save(Replace(intent with { Cues = [.. cues] }), new IntentArmed(this.GetPrimaryKeyString(), id));
    }

    private Intent Find(Guid id, long expectedRevision)
    {
        if (expectedRevision != Snapshot.Revision)
        { throw new RevisionConflictException($"Expected revision {expectedRevision}; current revision is {Snapshot.Revision}.", Snapshot.Revision); }
        return Array.Find(Snapshot.Intents, item => item.Id == id) ?? throw new ArgumentException("The intent does not exist.", nameof(id));
    }

    private IntentsState Replace(Intent next) =>
        Snapshot with { Revision = Snapshot.Revision + 1, Intents = [.. Snapshot.Intents.Select(item => item.Id == next.Id ? next : item)] };

    public Task<IntentsSnapshot> Read() => Task.FromResult(new IntentsSnapshot(Snapshot.Revision, Snapshot.Intents.ToArray()));
}
