using Microsoft.Extensions.AI;

namespace DigitalBrain.Framework.Contracts;

// By meaning when an embedding model is composed, by words otherwise. The catalog is small and lives
// in memory, so its vectors do too: they are rebuilt whenever the catalog changes.
public sealed class NeuronTypeSearch(ContractCatalog types, IEmbeddingGenerator<string, Embedding<float>>? embeddings) : IDisposable
{
    private const int MaxTake = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<NeuronType>? _indexed;
    private float[][] _vectors = [];

    public async Task<IReadOnlyList<NeuronTypeHit>> Search(string query, int take = 10, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ct.ThrowIfCancellationRequested();
        take = Math.Min(take, MaxTake);
        var current = types.Read();
        if (embeddings is null) { return ByWords(current, query, take); }
        if (current.Count == 0) { return []; }
        var vectors = await Index(embeddings, current, ct).ConfigureAwait(false);
        var queryVector = (await embeddings.GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false))[0].Vector.ToArray();
        if (queryVector.Length != vectors[0].Length)
        { throw new InvalidOperationException("The embedding provider returned a query vector of a different size than the type vectors."); }
        return [.. current.Select((type, index) => new NeuronTypeHit(type, Cosine(queryVector, vectors[index])))
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Type.Id, StringComparer.Ordinal).Take(take)];
    }

    public void Dispose() => _gate.Dispose();

    private async Task<float[][]> Index(IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<NeuronType> current, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(current, _indexed)) { return _vectors; }
            var generated = await generator.GenerateAsync(current.Select(type => type.Name + ": " + type.Description + "\n" + string.Join('\n', type.Methods)), cancellationToken: ct).ConfigureAwait(false);
            var vectors = generated.Select(embedding => embedding.Vector.ToArray()).ToArray();
            if (vectors.Length != current.Count || vectors[0].Length == 0 || vectors.Any(vector => vector.Length != vectors[0].Length))
            { throw new InvalidOperationException("The embedding provider returned an invalid set of type vectors."); }
            (_indexed, _vectors) = (current, vectors);
            return vectors;
        }
        finally { _gate.Release(); }
    }

    private static NeuronTypeHit[] ByWords(IReadOnlyList<NeuronType> current, string query, int take)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. current.Select(type => new NeuronTypeHit(type, terms.Count(term =>
                (type.Id + " " + type.Name + " " + type.Description + " " + string.Join(" ", type.Methods)).Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Where(hit => hit.Score > 0).OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Type.Id, StringComparer.Ordinal).Take(take)];
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
