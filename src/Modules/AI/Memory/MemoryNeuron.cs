using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.AI.Memory;

[ExportableState]
internal sealed record MemoryState(
    long Revision,
    MemoryEpisode[] Episodes,
    MemoryFact[] Facts,
    bool Synced = false)
{
    public MemoryState() : this(0, [], []) { }

    public MemoryNote[] Notes { get; init; } = [];
    public MemoryGraphEdge[] Edges { get; init; } = [];
}

[Neuron("ai.memory", State = "memory")]
internal sealed class MemoryNeuron : Neuron<MemoryState>, IMemory
{
    private const int MaxEpisodes = 256;
    private const int MaxFacts = 512;
    private const int MaxNotes = 512;
    private const int MaxEdges = 1024;
    private const double MinSemanticThreshold = 0.22;

    private readonly IMemoryEmbedder _embedder;

    public MemoryNeuron(IMemoryEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        _embedder = embedder;
    }

    public Task<MemorySnapshot> Read()
        => Task.FromResult(new MemorySnapshot(State.Revision, State.Episodes, State.Facts)
        {
            Notes = State.Notes,
            Edges = State.Edges,
        });

    public async Task<MemoryEpisode> CaptureEpisode(string agent, string summary, int importance = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        var clamped = Math.Clamp(importance, 1, 10);
        var episode = new MemoryEpisode(
            Guid.NewGuid().ToString("N"),
            agent.Trim(),
            summary.Trim(),
            clamped,
            Context.Time.GetUtcNow());
        var episodes = State.Episodes.Append(episode).TakeLast(MaxEpisodes).ToArray();
        var next = State with { Revision = State.Revision + 1, Episodes = episodes };
        await PersistStateAsync(next).ConfigureAwait(false);
        return episode;
    }

    public async Task<MemoryFact> RememberFact(string subject, string text, int importance = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var clamped = Math.Clamp(importance, 1, 10);
        var fact = new MemoryFact(
            Guid.NewGuid().ToString("N"),
            subject.Trim(),
            text.Trim(),
            clamped,
            Context.Time.GetUtcNow());
        var existing = State.Facts
            .Where(f => !string.Equals(f.Subject, fact.Subject, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(f.Text, fact.Text, StringComparison.OrdinalIgnoreCase))
            .Append(fact)
            .TakeLast(MaxFacts)
            .ToArray();
        var next = State with { Revision = State.Revision + 1, Facts = existing };
        await PersistStateAsync(next).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
        if (TryGetStore() is { } store)
        {
            await store.RecordNodeAsync(ToNode(fact)).ConfigureAwait(false);
        }
        return fact;
    }

    public async Task<MemoryNote> RememberPreference(string scope, string subject, string body, int importance = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "owner" : scope.Trim();
        var clamped = Math.Clamp(importance, 1, 10);
        // Caller-enforced trust: model/agent tool calls record as Agent trust until promoted by a person;
        // direct person/platform calls record as Owner trust.
        var trust = IsAgentCaller() ? MemoryTrust.Agent : MemoryTrust.Owner;
        var trimmedSubject = subject.Trim();
        var trimmedBody = body.Trim();
        var embedding = await _embedder.EmbedAsync($"{trimmedSubject}: {trimmedBody}").ConfigureAwait(false);
        var entities = ExtractEntities(normalizedScope, trimmedSubject);

        var note = new MemoryNote(
            Guid.NewGuid().ToString("N"),
            "preference",
            normalizedScope,
            trust,
            trimmedSubject,
            trimmedBody,
            clamped,
            Context.Time.GetUtcNow(),
            embedding,
            entities);

        var updatedNotes = State.Notes
            .Where(existing => !(string.Equals(existing.Scope, note.Scope, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Subject, note.Subject, StringComparison.OrdinalIgnoreCase)
                && existing.Trust <= note.Trust))
            .Append(note)
            .TakeLast(MaxNotes)
            .ToArray();

        var next = State with { Revision = State.Revision + 1, Notes = updatedNotes };
        await PersistStateAsync(next).ConfigureAwait(false);
        if (TryGetStore() is { } store)
        {
            await store.RecordNodeAsync(ToNode(note)).ConfigureAwait(false);
        }
        return note;
    }

    public async Task<MemoryNote> PromoteNote(string noteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        if (IsAgentCaller())
        {
            throw new DigitalBrain.Rules.RuleViolationException(
                new DigitalBrain.Rules.Rejected("PromoteNote", ["owner-only"], ["Only a person may promote a memory note to owner rule trust."]));
        }

        var target = State.Notes.FirstOrDefault(n => string.Equals(n.Id, noteId.Trim(), StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Memory note '{noteId}' was not found.");

        var promoted = target with { Trust = MemoryTrust.Owner };
        var updatedNotes = State.Notes.Select(n => n.Id == promoted.Id ? promoted : n).ToArray();
        var next = State with { Revision = State.Revision + 1, Notes = updatedNotes };
        await PersistStateAsync(next).ConfigureAwait(false);
        return promoted;
    }

    public async Task<MemoryGraphEdge> LinkEntities(string sourceEntity, string relation, string targetEntity, double weight = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntity);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetEntity);

        var edge = new MemoryGraphEdge(
            sourceEntity.Trim(),
            targetEntity.Trim(),
            relation.Trim(),
            Math.Clamp(weight, 0.1, 2.0));

        var updatedEdges = State.Edges
            .Where(e => !(string.Equals(e.SourceEntity, edge.SourceEntity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.TargetEntity, edge.TargetEntity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Relation, edge.Relation, StringComparison.OrdinalIgnoreCase)))
            .Append(edge)
            .TakeLast(MaxEdges)
            .ToArray();

        var next = State with { Revision = State.Revision + 1, Edges = updatedEdges };
        await PersistStateAsync(next).ConfigureAwait(false);

        if (TryGetStore() is { } store)
        {
            await store.RecordNodeAsync(new KnowledgeNode(edge.SourceEntity, edge.SourceEntity, "entity", new Dictionary<string, string>(StringComparer.Ordinal), null, 5, Context.Time.GetUtcNow())).ConfigureAwait(false);
            await store.RecordNodeAsync(new KnowledgeNode(edge.TargetEntity, edge.TargetEntity, "entity", new Dictionary<string, string>(StringComparer.Ordinal), null, 5, Context.Time.GetUtcNow())).ConfigureAwait(false);
            await store.RecordEdgeAsync(new KnowledgeEdge(edge.SourceEntity, edge.TargetEntity, edge.Relation, edge.Weight, null, Context.Time.GetUtcNow())).ConfigureAwait(false);
        }

        return edge;
    }

    public async Task<MemoryRecallHit[]> Recall(string query, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var pack = await RecallPack(query, activeScope: null, limit).ConfigureAwait(false);
        return pack.RecalledItems;
    }

    public async Task<MemoryRecallPack> RecallPack(string query, string? activeScope = null, int limit = 5)
    {
        await SyncAsync().ConfigureAwait(false);

        var inferredScope = string.IsNullOrWhiteSpace(activeScope) ? InferRepoScope(query) : activeScope.Trim();

        // Slot P2: Deterministic Owner-trusted rules matching global / owner / active repo scope.
        var ownerRules = State.Notes
            .Where(n => n.Trust == MemoryTrust.Owner && HybridMemoryScorer.ScopeMatches(n.Scope, inferredScope))
            .OrderByDescending(n => n.Importance)
            .ThenByDescending(n => n.RecordedAt)
            .Take(12)
            .ToArray();

        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return new MemoryRecallPack(ownerRules, []);
        }

        var terms = HybridMemoryScorer.Tokenize(query);
        var queryEmbedding = await _embedder.EmbedAsync(query).ConfigureAwait(false);
        var queryLocalEmbedding = queryEmbedding.Length == HybridMemoryEmbedder.LocalDimension
            ? queryEmbedding
            : HybridMemoryEmbedder.LocalDeterministicEmbed(query);

        var hits = new List<MemoryRecallHit>();

        // 1. Hybrid score over Notes (BM25 lexical + cosine similarity + 2-hop entity graph activation).
        foreach (var note in State.Notes)
        {
            var doc = $"{note.Scope} {note.Subject} {note.Body}";
            var lexical = HybridMemoryScorer.LexicalScore(doc, terms);
            var noteLocal = note.Embedding is { Length: HybridMemoryEmbedder.LocalDimension }
                ? note.Embedding
                : HybridMemoryEmbedder.LocalDeterministicEmbed($"{note.Subject}: {note.Body}");
            var cosine = note.Embedding is not null && note.Embedding.Length == queryEmbedding.Length
                ? HybridMemoryScorer.Cosine(queryEmbedding, note.Embedding)
                : HybridMemoryScorer.Cosine(queryLocalEmbedding, noteLocal);
            var graph = HybridMemoryScorer.GraphActivation(doc, note.Entities, terms, State.Edges);

            if (lexical <= 0 && cosine < MinSemanticThreshold && graph <= 0)
            {
                continue;
            }

            var raw = (0.45 * lexical) + (0.40 * cosine) + (0.15 * graph);
            var scopeBoost = HybridMemoryScorer.ScopeMatches(note.Scope, inferredScope) ? 1.25 : 0.9;
            var score = raw * (1.0 + (note.Importance / 20.0)) * scopeBoost;
            if (score > 0.08)
            {
                hits.Add(new MemoryRecallHit(note.Id, note.Kind, $"[{note.Scope}] {note.Subject}: {note.Body}", score));
            }
        }

        // 2. Hybrid score over Facts.
        foreach (var fact in State.Facts)
        {
            var doc = $"{fact.Subject} {fact.Text}";
            var lexical = HybridMemoryScorer.LexicalScore(doc, terms);
            var factVec = HybridMemoryEmbedder.LocalDeterministicEmbed(doc);
            var cosine = HybridMemoryScorer.Cosine(queryLocalEmbedding, factVec);
            var graph = HybridMemoryScorer.GraphActivation(doc, null, terms, State.Edges);

            if (lexical <= 0 && cosine < MinSemanticThreshold && graph <= 0)
            {
                continue;
            }

            var raw = lexical > 0
                ? Math.Max(lexical, (0.55 * lexical) + (0.30 * cosine) + (0.15 * graph))
                : (0.70 * cosine) + (0.30 * graph);
            var score = raw * (1.0 + (fact.Importance / 20.0));
            if (score > 0.08)
            {
                hits.Add(new MemoryRecallHit(fact.Id, "fact", $"{fact.Subject}: {fact.Text}", score));
            }
        }

        // 3. Hybrid score over Episodes.
        foreach (var episode in State.Episodes)
        {
            var doc = $"{episode.Agent} {episode.Summary}";
            var lexical = HybridMemoryScorer.LexicalScore(doc, terms);
            var epVec = HybridMemoryEmbedder.LocalDeterministicEmbed(episode.Summary);
            var cosine = HybridMemoryScorer.Cosine(queryLocalEmbedding, epVec);
            var graph = HybridMemoryScorer.GraphActivation(doc, null, terms, State.Edges);

            if (lexical <= 0 && cosine < MinSemanticThreshold && graph <= 0)
            {
                continue;
            }

            var raw = lexical > 0
                ? Math.Max(lexical, (0.55 * lexical) + (0.30 * cosine) + (0.15 * graph))
                : (0.70 * cosine) + (0.30 * graph);
            var score = raw * (1.0 + (episode.Importance / 20.0));
            if (score > 0.08)
            {
                hits.Add(new MemoryRecallHit(episode.Id, "episode", episode.Summary, score));
            }
        }

        // 4. Graph hits from the knowledge store replace the lexical hit for the same fact and add facts reached by relations.
        if (TryGetStore() is { } store)
        {
            foreach (var hit in await store.SemanticSearchAsync(query, limit).ConfigureAwait(false))
            {
                hits.RemoveAll(h => h.Id == hit.Node.Id);
                var text = hit.AssociativePath is { Count: > 1 } path ? $"{hit.Node.Label} (via {string.Join(" → ", path)})" : hit.Node.Label;
                hits.Add(new MemoryRecallHit(hit.Node.Id, "fact", text, hit.RelevanceScore));
            }
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return new MemoryRecallPack(ownerRules, ordered);
    }

    private Task PersistStateAsync(MemoryState next) => Save(next, new MemoryChanged(next.Revision, next.Episodes.Length, next.Facts.Length)
    {
        NoteCount = next.Notes.Length,
    });

    private IKnowledgeStore? TryGetStore()
    {
        try
        {
            return Get<IKnowledgeStore>(new NeuronId(Id.Brain, "ai.knowledge-store", KnowledgeGraphSource.StoreKey));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task SyncAsync()
    {
        if (State.Synced || TryGetStore() is not { } store) { return; }
        foreach (var fact in State.Facts)
        {
            await store.RecordNodeAsync(ToNode(fact)).ConfigureAwait(false);
        }
        foreach (var note in State.Notes)
        {
            await store.RecordNodeAsync(ToNode(note)).ConfigureAwait(false);
        }
        foreach (var edge in State.Edges)
        {
            await store.RecordNodeAsync(new KnowledgeNode(edge.SourceEntity, edge.SourceEntity, "entity", new Dictionary<string, string>(StringComparer.Ordinal), null, 5, Context.Time.GetUtcNow())).ConfigureAwait(false);
            await store.RecordNodeAsync(new KnowledgeNode(edge.TargetEntity, edge.TargetEntity, "entity", new Dictionary<string, string>(StringComparer.Ordinal), null, 5, Context.Time.GetUtcNow())).ConfigureAwait(false);
            await store.RecordEdgeAsync(new KnowledgeEdge(edge.SourceEntity, edge.TargetEntity, edge.Relation, edge.Weight, null, Context.Time.GetUtcNow())).ConfigureAwait(false);
        }
        var next = State with { Revision = State.Revision + 1, Synced = true };
        await PersistStateAsync(next).ConfigureAwait(false);
    }

    private static KnowledgeNode ToNode(MemoryFact fact)
        => new(fact.Id, $"{fact.Subject}: {fact.Text}", "fact",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["subject"] = fact.Subject }, null, fact.Importance, fact.RecordedAt);

    private static KnowledgeNode ToNode(MemoryNote note)
        => new(note.Id, $"[{note.Scope}] {note.Subject}: {note.Body}", "fact",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subject"] = note.Subject,
                ["scope"] = note.Scope,
                ["trust"] = note.Trust.ToString(),
            },
            null,
            note.Importance,
            note.RecordedAt);

    private bool IsAgentCaller() => Context.Caller?.Actor is DigitalBrain.Access.Actor.Agent;

    private static string[] ExtractEntities(string scope, string subject)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope) && !string.Equals(scope, "owner", StringComparison.OrdinalIgnoreCase) && !string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(scope);
        }
        if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(subject);
        }
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? InferRepoScope(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) { return null; }
        foreach (var token in query.Split([' ', '\t', '\r', '\n', ',', '.', ';', '!', '?', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("repo:", StringComparison.OrdinalIgnoreCase) && token.Length > 5)
            {
                return token;
            }
        }
        return null;
    }
}
