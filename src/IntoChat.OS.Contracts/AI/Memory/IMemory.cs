using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Memory;

public enum MemoryTrust
{
    Agent = 0,
    Owner = 1,
}

public sealed record MemoryEpisode(
    string Id,
    string Agent,
    string Summary,
    int Importance,
    DateTimeOffset RecordedAt);

public sealed record MemoryFact(
    string Id,
    string Subject,
    string Text,
    int Importance,
    DateTimeOffset RecordedAt);

public sealed record MemoryGraphEdge(
    string SourceEntity,
    string TargetEntity,
    string Relation,
    double Weight = 1.0);

public sealed record MemoryNote(
    string Id,
    string Kind,
    string Scope,
    MemoryTrust Trust,
    string Subject,
    string Body,
    int Importance,
    DateTimeOffset RecordedAt,
    float[]? Embedding = null,
    string[]? Entities = null);

public sealed record MemoryRecallHit(
    string Id,
    string Kind,
    string Text,
    double Score);

public sealed record MemoryRecallPack(
    MemoryNote[] OwnerRules,
    MemoryRecallHit[] RecalledItems);

public sealed record MemorySnapshot(
    long Revision,
    MemoryEpisode[] Episodes,
    MemoryFact[] Facts)
{
    public MemoryNote[] Notes { get; init; } = [];
    public MemoryGraphEdge[] Edges { get; init; } = [];
}

[DigitalBrain.Signal("ai.memory.changed")]
public sealed record MemoryChanged(
    long Revision,
    int EpisodeCount,
    int FactCount) : DigitalBrain.Signal
{
    public int NoteCount { get; init; }
}

[NeuronAlias("ai.memory"), DefaultNeuronType("ai.memory")]
[Description("Durable hybrid memory of owner preferences, facts, episodes, and entity links for a brain, persisted locally with zero external database dependency.")]
public interface IMemory
{
    const string DefaultKey = "memory";

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<MemorySnapshot> Read();

    [Description("Record a summarized conversation episode.")]
    Task<MemoryEpisode> CaptureEpisode(string agent, string summary, int importance = 5);

    [Description("Record a durable fact about the user or brain.")]
    Task<MemoryFact> RememberFact(string subject, string text, int importance = 5);

    [Description("Store a durable preference, coding convention, or correction under scope ('owner', 'global', or 'repo:<name>').")]
    Task<MemoryNote> RememberPreference(string scope, string subject, string body, int importance = 8);

    [Description("Promote a memory note to owner-confirmed rule trust.")]
    Task<MemoryNote> PromoteNote(string noteId);

    [Description("Link two entities in the memory graph (for example 'repo:os', 'uses', 'xUnit v3').")]
    Task<MemoryGraphEdge> LinkEntities(string sourceEntity, string relation, string targetEntity, double weight = 1.0);

    [Description("Recall the most relevant facts, preferences, and episodes matching the query.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<MemoryRecallHit[]> Recall(string query, int limit = 5);

    [Description("Recall both scoped owner rules (Slot P2) and hybrid top-K memories (Slot P6) for a turn.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<MemoryRecallPack> RecallPack(string query, string? activeScope = null, int limit = 5);
}
