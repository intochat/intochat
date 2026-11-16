using System.IO.Hashing;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Models.Ollama;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Memory;

internal interface IMemoryEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

internal sealed class HybridMemoryEmbedder(
    IOptionsMonitor<AIOptions>? options = null,
    ILocalOllamaProbe? localProbe = null,
    IHttpClientFactory? httpClients = null) : IMemoryEmbedder
{
    public const int LocalDimension = 384;
    private static readonly TimeSpan EmbedTimeout = TimeSpan.FromMilliseconds(200);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new float[LocalDimension];
        }

        var remote = await TryEmbedWithNomicAsync(text, ct).ConfigureAwait(false);
        return remote ?? LocalDeterministicEmbed(text);
    }

    private async Task<float[]?> TryEmbedWithNomicAsync(string text, CancellationToken ct)
    {
        var configuredEndpoint = options?.CurrentValue.Ollama.Endpoint;
        string? endpoint = null;
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            endpoint = configuredEndpoint.Trim();
        }
        else if (localProbe is not null)
        {
            var detected = await localProbe.Detect(ct).ConfigureAwait(false);
            if (detected is { HasNomicEmbed: true })
            {
                endpoint = detected.Endpoint;
            }
        }

        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(EmbedTimeout);

        HttpClient? owned = null;
        try
        {
            var client = httpClients?.CreateClient("ollama-memory-embed") ?? (owned = new HttpClient());
            var embedUri = new Uri(baseUri, "/api/embed");
            using var response = await client.PostAsJsonAsync(
                embedUri,
                new { model = "nomic-embed-text", input = text },
                cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("embeddings", out var embeddings)
                && embeddings.ValueKind == JsonValueKind.Array
                && embeddings.GetArrayLength() > 0)
            {
                var first = embeddings[0];
                if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0)
                {
                    var vector = new float[first.GetArrayLength()];
                    var idx = 0;
                    foreach (var value in first.EnumerateArray())
                    {
                        vector[idx++] = value.GetSingle();
                    }
                    NormalizeInPlace(vector);
                    return vector;
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    // Deterministic 384-dimensional feature-hashed word + character 3/4-gram embedding with L2 normalization.
    // Captures morphological and subword similarity ("deleting" <-> "delete", "repositories" <-> "repo")
    // in <0.1ms with zero external runtime dependencies.
    public static float[] LocalDeterministicEmbed(string text)
    {
        var vector = new float[LocalDimension];
        var normalized = text.ToLowerInvariant();
        var tokens = HybridMemoryScorer.Tokenize(normalized);

        foreach (var token in tokens)
        {
            AccumulateFeature(vector, "w:" + token, 2.0f);
            var stem = Stem(token);
            if (!string.Equals(stem, token, StringComparison.Ordinal))
            {
                AccumulateFeature(vector, "s:" + stem, 1.6f);
            }

            var padded = $"<{token}>";
            for (var n = 3; n <= 4; n++)
            {
                if (padded.Length < n) { continue; }
                for (var i = 0; i <= padded.Length - n; i++)
                {
                    AccumulateFeature(vector, padded.Substring(i, n), 0.75f);
                }
            }
        }

        NormalizeInPlace(vector);
        return vector;
    }

    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal)) { return word[..^3]; }
        if (word.Length > 5 && word.EndsWith("ies", StringComparison.Ordinal)) { return word[..^3] + "y"; }
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal)) { return word[..^2]; }
        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal)) { return word[..^2]; }
        if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal)) { return word[..^1]; }
        return word;
    }

    private static void AccumulateFeature(float[] vector, string feature, float weight)
    {
        var bytes = Encoding.UTF8.GetBytes(feature);
        var hash = XxHash64.HashToUInt64(bytes);
        var index = (int)(hash % (ulong)vector.Length);
        var sign = ((hash >> 32) & 1UL) == 0 ? 1.0f : -1.0f;
        vector[index] += sign * weight;
    }

    internal static void NormalizeInPlace(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSq += (double)vector[i] * vector[i];
        }
        if (sumSq <= 1e-12)
        {
            return;
        }
        var inv = (float)(1.0 / Math.Sqrt(sumSq));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= inv;
        }
    }
}

internal static class HybridMemoryScorer
{
    public static string[] Tokenize(string text)
        => text.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static w => w.Length >= 2)
            .Select(static w => w.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static double Cosine(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }
        return Math.Clamp(dot, 0.0, 1.0);
    }

    public static double LexicalScore(string document, string[] queryTerms)
    {
        if (queryTerms.Length == 0 || string.IsNullOrWhiteSpace(document))
        {
            return 0;
        }

        var matched = 0;
        foreach (var term in queryTerms)
        {
            if (document.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
        }
        return (double)matched / queryTerms.Length;
    }

    public static double GraphActivation(string document, string[]? noteEntities, string[] queryTerms, MemoryGraphEdge[] edges)
    {
        if (edges.Length == 0 || queryTerms.Length == 0)
        {
            return 0;
        }

        // Seed entities mentioned in the query (1-hop & 2-hop spreading activation).
        var frontier = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            if (MatchesAny(edge.SourceEntity, queryTerms))
            {
                frontier[edge.SourceEntity] = 1.0;
            }
            if (MatchesAny(edge.TargetEntity, queryTerms))
            {
                frontier[edge.TargetEntity] = 1.0;
            }
        }

        if (frontier.Count == 0)
        {
            return 0;
        }

        // 2-hop propagation with decay 0.65 per hop.
        for (var hop = 0; hop < 2; hop++)
        {
            var snapshot = frontier.ToArray();
            foreach (var (entity, activation) in snapshot)
            {
                foreach (var edge in edges)
                {
                    if (string.Equals(edge.SourceEntity, entity, StringComparison.OrdinalIgnoreCase))
                    {
                        var next = activation * 0.65 * Math.Clamp(edge.Weight, 0.1, 2.0);
                        if (!frontier.TryGetValue(edge.TargetEntity, out var current) || next > current)
                        {
                            frontier[edge.TargetEntity] = next;
                        }
                    }
                    else if (string.Equals(edge.TargetEntity, entity, StringComparison.OrdinalIgnoreCase))
                    {
                        var next = activation * 0.65 * Math.Clamp(edge.Weight, 0.1, 2.0);
                        if (!frontier.TryGetValue(edge.SourceEntity, out var current) || next > current)
                        {
                            frontier[edge.SourceEntity] = next;
                        }
                    }
                }
            }
        }

        double best = 0;
        foreach (var (entity, activation) in frontier)
        {
            var hit = (noteEntities is not null && noteEntities.Any(e => string.Equals(e, entity, StringComparison.OrdinalIgnoreCase)))
                || document.Contains(entity, StringComparison.OrdinalIgnoreCase);
            if (hit && activation > best)
            {
                best = activation;
            }
        }
        return Math.Clamp(best, 0.0, 1.0);
    }

    public static bool ScopeMatches(string noteScope, string? activeScope)
    {
        if (string.IsNullOrWhiteSpace(noteScope))
        {
            return true;
        }
        if (string.Equals(noteScope, "owner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(noteScope, "global", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(activeScope)
            && string.Equals(noteScope, activeScope.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAny(string entity, string[] queryTerms)
        => queryTerms.Any(term => entity.Contains(term, StringComparison.OrdinalIgnoreCase)
            || term.Contains(entity, StringComparison.OrdinalIgnoreCase));
}
