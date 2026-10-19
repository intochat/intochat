using System.ComponentModel;
using IntoChat.OS;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace IntoChat.OS.AI.Agents.Facts;

public enum FactKind { Profile, Fact, Insight }

public enum FactScope { Private, Circle }

[GenerateSerializer, Alias("facts.fact")]
public sealed record Fact([property: Id(0)] Guid Id, [property: Id(1)] FactKind Kind, [property: Id(2)] string Text,
    [property: Id(3)] string Subject, [property: Id(4)] double Confidence, [property: Id(5)] int Importance,
    [property: Id(6)] DateTimeOffset ValidFrom, [property: Id(7)] DateTimeOffset? ValidTo, [property: Id(8)] string? InvalidReason,
    [property: Id(9)] DateTimeOffset Recorded, [property: Id(10)] string SourcePublisher, [property: Id(11)] long SourceSequence,
    [property: Id(12)] string PromptVersion, [property: Id(13)] FactScope Scope, [property: Id(14)] Guid[] Links, [property: Id(15)] bool Hidden);

// DedupKey: episode id + ordinal, so Assert is idempotent.
[GenerateSerializer, Alias("facts.assertion")]
public sealed record FactAssertion([property: Id(0)] FactKind Kind, [property: Id(1)] string Text, [property: Id(2)] string Subject,
    [property: Id(3)] double Confidence, [property: Id(4)] int Importance, [property: Id(5)] DateTimeOffset ValidFrom,
    [property: Id(6)] string SourcePublisher, [property: Id(7)] long SourceSequence, [property: Id(8)] string PromptVersion,
    [property: Id(9)] Guid[] Links, [property: Id(10)] string DedupKey);

[GenerateSerializer, Alias("facts.snapshot")]
public sealed record FactsSnapshot([property: Id(0)] long Revision, [property: Id(1)] Fact[] Facts);

[Alias("facts"), Orleans.Metadata.DefaultGrainType("facts")]
public interface IFacts : INeuron
{
    [Description("Record a durable fact about the user. Returns the existing fact when the dedup key was seen before.")]
    Task<Guid> Assert(FactAssertion assertion);

    [Description("End a fact's validity with a reason; the fact stays in history.")]
    Task Invalidate(Guid id, string reason, Guid? supersededBy, long expectedRevision);

    [Description("Hide a fact from recall and rendering (\"forget this\"); it stays in the journal.")]
    Task Forget(Guid id, string reason, long expectedRevision);

    [ReadOnly, AlwaysInterleave]
    Task<FactsSnapshot> Read();

    // ValidTo == null && !Hidden
    [ReadOnly, AlwaysInterleave]
    Task<Fact[]> Current();

    static string Key(string brainId) => $"{brainId}/memory/facts";

    const int Cap = 512;
    const int TextLimit = 2_000;
    const int SubjectLimit = 200;
}
