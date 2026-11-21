using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.AI.Venn;

[Neuron("ai.venn")]
internal sealed class VennSystemOneNeuron(IMemoryEmbedder? embedder = null, TimeProvider? clock = null) : Neuron<VennSystemOneState>, IVennSystemOne
{
    private const int MaxPrecedents = 4096;

    private async Task<float[]> EmbedTextAsync(string contextText)
    {
        if (string.IsNullOrWhiteSpace(contextText))
        {
            return [];
        }

        return embedder is not null
            ? await embedder.EmbedAsync(contextText).ConfigureAwait(false)
            : HybridMemoryEmbedder.LocalDeterministicEmbed(contextText);
    }

    public async Task<VennSystemOneState> LearnFromText(
        string domain,
        string contextText,
        string action,
        double outcomeScore = 1.0,
        bool autoCompile = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextText);
        var vector = await EmbedTextAsync(contextText).ConfigureAwait(false);
        var state = await RecordOutcome(domain, contextText, vector, action, outcomeScore).ConfigureAwait(false);
        return autoCompile ? await CompileHead().ConfigureAwait(false) : state;
    }

    public async Task<VennSystemOneState> RecordOutcome(
        string domain,
        string contextSummary,
        float[] embedding,
        string action,
        double outcomeScore = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
        {
            throw new ArgumentException("Embedding vector must be non-empty.", nameof(embedding));
        }

        var normalized = Normalize(embedding);
        var entry = new VennPrecedent(
            Id: Guid.NewGuid().ToString("N"),
            Domain: string.IsNullOrWhiteSpace(domain) ? "default" : domain.Trim(),
            ContextSummary: contextSummary ?? string.Empty,
            Embedding: normalized,
            Action: action.Trim(),
            OutcomeScore: Math.Clamp(outcomeScore, -1.0, 1.0),
            RecordedAt: clock?.GetUtcNow() ?? default);

        var existing = State.Precedents ?? [];
        var updated = existing.Length >= MaxPrecedents
            ? [.. existing.Skip(existing.Length - MaxPrecedents + 1), entry]
            : (VennPrecedent[])[.. existing, entry];

        var next = State with
        {
            Version = State.Version + 1,
            Precedents = updated,
        };

        await Save(next, new VennPrecedentRecorded(Id.FullKey(), next.Version, entry.Domain, entry.Action, entry.OutcomeScore));
        return next;
    }

    public async Task<VennSystemOneState> CompileHead(double? abstentionThreshold = null)
    {
        var precedents = State.Precedents ?? [];
        var threshold = abstentionThreshold is { } t ? Math.Clamp(t, 0.05, 0.99) : State.AbstentionThreshold;

        var prototypes = new List<VennClassPrototype>();
        foreach (var group in precedents.GroupBy(static p => p.Action, StringComparer.Ordinal))
        {
            var items = group.ToList();
            var dim = items[0].Embedding.Length;
            var sum = new double[dim];
            var totalWeight = 0.0;
            var outcomeSum = 0.0;

            foreach (var item in items)
            {
                if (item.Embedding.Length != dim) { continue; }
                // Reward-weighted centroid: positive outcomes reinforce, negative outcomes repel.
                var weight = Math.Max(0.1, item.OutcomeScore);
                for (var i = 0; i < dim; i++)
                {
                    sum[i] += item.Embedding[i] * weight;
                }
                totalWeight += weight;
                outcomeSum += item.OutcomeScore;
            }

            if (totalWeight <= 0) { continue; }
            var centroid = new float[dim];
            for (var i = 0; i < dim; i++)
            {
                centroid[i] = (float)(sum[i] / totalWeight);
            }

            prototypes.Add(new VennClassPrototype(
                Action: group.Key,
                Centroid: Normalize(centroid),
                SampleCount: items.Count,
                MeanOutcome: outcomeSum / items.Count));
        }

        var next = State with
        {
            Version = State.Version + 1,
            CompiledVersion = State.Version + 1,
            AbstentionThreshold = threshold,
            Prototypes = [.. prototypes],
        };

        await Save(next, new VennHeadCompiled(Id.FullKey(), next.CompiledVersion, prototypes.Count, precedents.Length));
        return next;
    }

    public Task<VennChoiceResult> Choose(float[] queryEmbedding, string[]? candidateActions = null)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        var prototypes = State.Prototypes ?? [];
        if (prototypes.Length == 0 || queryEmbedding.Length == 0)
        {
            return Task.FromResult(new VennChoiceResult(null, 0.0, Abstained: true, new Dictionary<string, double>()));
        }

        var query = Normalize(queryEmbedding);
        var allowed = candidateActions is { Length: > 0 }
            ? candidateActions.ToHashSet(StringComparer.Ordinal)
            : null;

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        string? bestAction = null;
        var bestScore = double.NegativeInfinity;

        foreach (var proto in prototypes)
        {
            if (allowed is not null && !allowed.Contains(proto.Action)) { continue; }
            if (proto.Centroid.Length != query.Length) { continue; }
            var sim = Cosine(query, proto.Centroid);
            scores[proto.Action] = sim;
            if (sim > bestScore)
            {
                bestScore = sim;
                bestAction = proto.Action;
            }
        }

        if (bestAction is null || bestScore < State.AbstentionThreshold)
        {
            return Task.FromResult(new VennChoiceResult(null, Math.Max(0.0, bestScore), Abstained: true, scores));
        }

        return Task.FromResult(new VennChoiceResult(bestAction, bestScore, Abstained: false, scores));
    }

    public async Task<VennChoiceResult> ChooseForText(string contextText, string[]? candidateActions = null)
    {
        var vector = await EmbedTextAsync(contextText).ConfigureAwait(false);
        return await Choose(vector, candidateActions).ConfigureAwait(false);
    }

    public async Task<VennScoreResult> ScoreForText(string contextText)
    {
        var vector = await EmbedTextAsync(contextText).ConfigureAwait(false);
        return await Score(vector).ConfigureAwait(false);
    }

    public async Task<VennNoulResult> NoulForText(string contextText, string positiveAction)
    {
        var vector = await EmbedTextAsync(contextText).ConfigureAwait(false);
        return await Noul(vector, positiveAction).ConfigureAwait(false);
    }

    public Task<VennScoreResult> Score(float[] queryEmbedding)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        var precedents = State.Precedents ?? [];
        if (precedents.Length == 0 || queryEmbedding.Length == 0)
        {
            return Task.FromResult(new VennScoreResult(0.0, 0.0, Abstained: true, 0));
        }

        var query = Normalize(queryEmbedding);
        var weightedScore = 0.0;
        var weightSum = 0.0;
        var maxSim = 0.0;

        foreach (var p in precedents)
        {
            if (p.Embedding.Length != query.Length) { continue; }
            var sim = Math.Max(0.0, Cosine(query, p.Embedding));
            if (sim > maxSim) { maxSim = sim; }
            if (sim < 0.2) { continue; }
            var w = sim * sim;
            weightedScore += p.OutcomeScore * w;
            weightSum += w;
        }

        if (weightSum <= 0 || maxSim < State.AbstentionThreshold)
        {
            return Task.FromResult(new VennScoreResult(0.0, maxSim, Abstained: true, precedents.Length));
        }

        return Task.FromResult(new VennScoreResult(weightedScore / weightSum, maxSim, Abstained: false, precedents.Length));
    }

    public async Task<VennNoulResult> Noul(float[] queryEmbedding, string positiveAction)
    {
        var choice = await Choose(queryEmbedding);
        if (choice.Abstained || !choice.Scores.TryGetValue(positiveAction, out var rawSim))
        {
            return new VennNoulResult(Positive: false, Probability: 0.5, Abstained: true);
        }

        var prob = Math.Clamp((rawSim + 1.0) * 0.5, 0.0, 1.0);
        return new VennNoulResult(Positive: string.Equals(choice.ChosenAction, positiveAction, StringComparison.Ordinal), Probability: prob, Abstained: false);
    }

    public Task<VennSystemOneState> ReadStatus() => Task.FromResult(State);

    private static float[] Normalize(float[] vector)
    {
        var sumSq = 0.0;
        for (var i = 0; i < vector.Length; i++) { sumSq += (double)vector[i] * vector[i]; }
        if (sumSq <= 1e-12) { return [.. vector]; }
        var inv = 1.0 / Math.Sqrt(sumSq);
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) { result[i] = (float)(vector[i] * inv); }
        return result;
    }

    private static double Cosine(float[] a, float[] b)
    {
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; }
        return dot;
    }
}
