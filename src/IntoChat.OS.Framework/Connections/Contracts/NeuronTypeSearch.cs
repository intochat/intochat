using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Connections.Contracts;

// What the caller's brain can search and call: the kernel's neuron types plus the live capabilities
// each source reports, ranked by meaning when an embedding model is composed, by words otherwise.
// Vectors are cached per entry (keyed by id + embedded text), so only new entries are embedded.
public sealed class NeuronTypeSearch(
    IBrainObserver kernel,
    IEnumerable<IRuntimeCapabilitySource> live,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings,
    ILogger<NeuronTypeSearch>? logger = null) : IDisposable
{
    private const int MaxTake = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    public static NeuronTypeSearch Create(IServiceProvider services) => new(
        services.GetRequiredService<IBrainObserver>(), services.GetServices<IRuntimeCapabilitySource>(), Embeddings(services),
        services.GetService<ILogger<NeuronTypeSearch>>());

    // Live capabilities belong to a brain, so an un-stamped call reads the kernel types alone. A source
    // that throws is logged and skipped so the kernel types stay searchable.
    public async Task<IReadOnlyList<NeuronType>> Capabilities(CancellationToken ct)
    {
        List<NeuronType> entries = [.. (await kernel.Types(ct).ConfigureAwait(false)).Where(type => type.Kind == TypeKind.Neuron).Select(NeuronType.From)];
        if (!RuntimeCallerContext.TryGet(out _)) { return entries; }
        var brain = BrainScope.CurrentId();
        foreach (var source in live)
        {
            try { entries.AddRange(await source.Read(brain, ct).ConfigureAwait(false)); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger?.LogWarning(error, "Capability source {Source} failed for brain {Brain}", source.GetType().Name, brain); }
        }
        return entries;
    }

    public async Task<IReadOnlyList<NeuronTypeHit>> Search(string query, int take = 10, CancellationToken ct = default)
        => await Search(query, await Capabilities(ct).ConfigureAwait(false), take, ct).ConfigureAwait(false);

    // queryVector is the query already embedded by the same generator, for a caller that ranks more than one corpus by it.
    public async Task<IReadOnlyList<NeuronTypeHit>> Search(string query, IReadOnlyList<NeuronType> entries, int take = 10, CancellationToken ct = default,
        float[]? queryVector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ct.ThrowIfCancellationRequested();
        take = Math.Min(take, MaxTake);
        if (embeddings is null) { return ByWords(entries, query, take); }
        if (entries.Count == 0) { return []; }
        var vectors = await Vectors(entries, ct).ConfigureAwait(false);
        queryVector ??= (await embeddings.GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false))[0].Vector.ToArray();
        if (queryVector.Length != vectors[0].Length)
        { throw new InvalidOperationException("The embedding provider returned a query vector of a different size than the type vectors."); }
        return [.. entries.Select((type, index) => new NeuronTypeHit(type, Cosine(queryVector, vectors[index])))
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Type.Id, StringComparer.Ordinal).Take(take)];
    }

    public void Dispose() => _gate.Dispose();

    private static IEmbeddingGenerator<string, Embedding<float>>? Embeddings(IServiceProvider services)
    {
        try { return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>(); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<float[][]> Vectors(IReadOnlyList<NeuronType> entries, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var missing = entries.Where(entry => !_vectors.ContainsKey(Key(entry))).ToArray();
            if (missing.Length > 0)
            {
                var generated = await embeddings!.GenerateAsync(missing.Select(Text), cancellationToken: ct).ConfigureAwait(false);
                var vectors = generated.Select(embedding => embedding.Vector.ToArray()).ToArray();
                if (vectors.Length != missing.Length || vectors[0].Length == 0 || vectors.Any(vector => vector.Length != vectors[0].Length))
                { throw new InvalidOperationException("The embedding provider returned an invalid set of type vectors."); }
                for (var i = 0; i < missing.Length; i++) { _vectors[Key(missing[i])] = vectors[i]; }
            }
            return entries.Select(entry => _vectors[Key(entry)]).ToArray();
        }
        finally { _gate.Release(); }
    }

    private static string Key(NeuronType entry) => entry.Id + "\n" + Text(entry);

    private static string Text(NeuronType entry) => entry.Name + ": " + entry.Description + "\n" + string.Join('\n', entry.Methods);

    private static NeuronTypeHit[] ByWords(IReadOnlyList<NeuronType> entries, string query, int take)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. entries.Select(type => new NeuronTypeHit(type, terms.Count(term =>
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
