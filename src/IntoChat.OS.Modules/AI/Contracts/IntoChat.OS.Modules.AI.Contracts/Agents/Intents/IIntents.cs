using DigitalBrain;
using DigitalBrain.Contracts;
using System.ComponentModel;
using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Agents.Intents;

public enum IntentState { Heard, Rephrased, Confirmed, Dispatched, Waiting, Done, Abandoned }

[GenerateSerializer, Alias("intents.draft")]
public sealed record IntentDraft([property: Id(0)] string RequestId, [property: Id(1)] string Heard,
    [property: Id(2)] string Conversation, [property: Id(3)] Guid? Continues = null);

[GenerateSerializer, Alias("intents.brief")]
public sealed record IntentBrief([property: Id(0)] string Goal, [property: Id(1)] string[] Inputs,
    [property: Id(2)] string[] Constraints, [property: Id(3)] string[] Acceptance);

// Kind is "signal" or "time".
[GenerateSerializer, Alias("intents.cue")]
public sealed record Cue([property: Id(0)] string Kind, [property: Id(1)] string PublisherKey,
    [property: Id(2)] string SignalAlias, [property: Id(3)] string? Field, [property: Id(4)] string? EqualTo,
    [property: Id(5)] DateTimeOffset? At);

[GenerateSerializer, Alias("intents.transition")]
public sealed record IntentTransition([property: Id(0)] IntentState State, [property: Id(1)] DateTimeOffset At,
    [property: Id(2)] string RequestId, [property: Id(3)] string Reason);

[GenerateSerializer, Alias("intents.intent")]
public sealed record Intent([property: Id(0)] Guid Id, [property: Id(1)] string Heard, [property: Id(2)] string Conversation,
    [property: Id(3)] string RequestId, [property: Id(4)] IntentBrief? Brief, [property: Id(5)] double Confidence,
    [property: Id(6)] IntentState State, [property: Id(7)] Cue[] Cues, [property: Id(8)] string[] Links,
    [property: Id(9)] IntentTransition[] History);

[GenerateSerializer, Alias("intents.snapshot")]
public sealed record IntentsSnapshot([property: Id(0)] long Revision, [property: Id(1)] Intent[] Intents);

[NeuronAlias("intents"), DefaultNeuronType("intents")]
public interface IIntents
{
    [Description("Record what the user asked as an intent. Returns the existing intent when the request id was heard before.")]
    Task<Guid> Declare(IntentDraft draft);

    Task Rephrase(Guid id, IntentBrief brief, double confidence, long expectedRevision);

    [Description("Move an intent to a new state: Confirmed, Dispatched, Waiting, Done or Abandoned.")]
    Task Transition(Guid id, IntentState to, string reason, long expectedRevision);

    Task Arm(Guid id, Cue[] cues, long expectedRevision);

    [DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves]
    Task<IntentsSnapshot> Read();

    static string Key(string brainId) => $"{brainId}/memory/intents";

    const int ActiveCap = 256;
    const int RequestIdLimit = 200;
    const int HeardLimit = 8_000;
}
