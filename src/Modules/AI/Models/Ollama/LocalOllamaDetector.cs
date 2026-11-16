using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Models.Ollama;

public sealed record LocalOllamaDetection(
    string Endpoint,
    string PreferredModel,
    string[] InstalledModels,
    bool HasNomicEmbed);

public interface ILocalOllamaProbe
{
    Task<LocalOllamaDetection?> Detect(CancellationToken ct = default);
}

internal sealed class HttpLocalOllamaProbe : ILocalOllamaProbe
{
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    public const string PreferredCoderModel = "qwen2.5-coder:14b";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HitCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissCacheDuration = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory? _httpClients;
    private readonly TimeProvider _clock;
    private readonly Uri? _explicitBaseUri;
    private readonly IOptionsMonitor<AIOptions>? _options;
    private readonly object _gate = new();
    private (string Endpoint, LocalOllamaDetection? Value, DateTimeOffset ExpiresAt)? _cached;

    [ActivatorUtilitiesConstructor]
    public HttpLocalOllamaProbe(
        IHttpClientFactory? httpClients = null,
        TimeProvider? clock = null,
        IOptionsMonitor<AIOptions>? options = null)
        : this(httpClients, clock, baseUri: null, options)
    {
    }

    internal HttpLocalOllamaProbe(IHttpClientFactory? httpClients, TimeProvider? clock, Uri? baseUri)
        : this(httpClients, clock, baseUri, null)
    {
    }

    private HttpLocalOllamaProbe(IHttpClientFactory? httpClients, TimeProvider? clock, Uri? baseUri, IOptionsMonitor<AIOptions>? options)
    {
        _httpClients = httpClients;
        _clock = clock ?? TimeProvider.System;
        _explicitBaseUri = baseUri;
        _options = options;
    }

    public async Task<LocalOllamaDetection?> Detect(CancellationToken ct = default)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE"), "1", StringComparison.Ordinal))
        {
            return null;
        }

        var (endpoint, tagsUri) = ResolveTarget();
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } hit && string.Equals(hit.Endpoint, endpoint, StringComparison.Ordinal) && now < hit.ExpiresAt)
            {
                return hit.Value;
            }
        }

        var detected = await ProbeOnce(endpoint, tagsUri, ct).ConfigureAwait(false);
        var ttl = detected is not null ? HitCacheDuration : MissCacheDuration;
        lock (_gate)
        {
            _cached = (endpoint, detected, _clock.GetUtcNow() + ttl);
        }
        return detected;
    }

    private (string Endpoint, Uri TagsUri) ResolveTarget()
    {
        var configured = _options?.CurrentValue.Ollama.Endpoint;
        var root = _explicitBaseUri
            ?? (Uri.TryCreate(configured, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? parsed : null)
            ?? new Uri(DefaultEndpoint);
        var endpoint = root.GetLeftPart(UriPartial.Authority);
        return (endpoint, new Uri(root, "/api/tags"));
    }

    private async Task<LocalOllamaDetection?> ProbeOnce(string endpoint, Uri tagsUri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);

        HttpClient? owned = null;
        try
        {
            var client = _httpClients?.CreateClient("ollama-local-probe") ?? (owned = new HttpClient());
            using var response = await client.GetAsync(tagsUri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            return ParseTags(endpoint, doc.RootElement);
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

    internal static LocalOllamaDetection? ParseTags(string endpoint, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("models", out var modelsElement)
            || modelsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var allTags = new List<string>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { continue; }
            if (item.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
            {
                allTags.Add(name.Trim());
            }
            else if (item.TryGetProperty("model", out var modelProp) && modelProp.GetString() is { Length: > 0 } modelName)
            {
                allTags.Add(modelName.Trim());
            }
        }

        if (allTags.Count == 0)
        {
            return null;
        }

        var hasNomic = allTags.Any(static tag => tag.StartsWith("nomic-embed-text", StringComparison.OrdinalIgnoreCase));
        var chatTags = allTags.Where(static tag => !IsEmbeddingModel(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (chatTags.Length == 0)
        {
            return null;
        }

        var preferred = SelectPreferredModel(chatTags);
        return new LocalOllamaDetection(endpoint, preferred, chatTags, hasNomic);
    }

    internal static bool IsCoderModelTag(string tag)
        => tag.Contains("coder", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("codellama", StringComparison.OrdinalIgnoreCase);

    internal static string SelectPreferredModel(IReadOnlyList<string> chatTags)
    {
        var defaultChat = ModelCatalog.DefaultChatModelId;
        return chatTags.FirstOrDefault(t => string.Equals(t, defaultChat, StringComparison.OrdinalIgnoreCase))
            ?? chatTags.FirstOrDefault(static t => !IsCoderModelTag(t) && t.StartsWith("qwen3.5", StringComparison.OrdinalIgnoreCase))
            ?? chatTags.FirstOrDefault(static t => !IsCoderModelTag(t))
            ?? SelectPreferredCoderModel(chatTags);
    }

    internal static string SelectPreferredCoderModel(IReadOnlyList<string> chatTags)
    {
        return chatTags.FirstOrDefault(static t => string.Equals(t, PreferredCoderModel, StringComparison.OrdinalIgnoreCase))
            ?? chatTags.FirstOrDefault(static t => t.StartsWith("qwen2.5-coder:14b", StringComparison.OrdinalIgnoreCase))
            ?? chatTags.FirstOrDefault(static t => t.StartsWith("qwen2.5-coder", StringComparison.OrdinalIgnoreCase))
            ?? chatTags.FirstOrDefault(static t => t.StartsWith("qwen3-coder", StringComparison.OrdinalIgnoreCase))
            ?? chatTags.FirstOrDefault(IsCoderModelTag)
            ?? chatTags[0];
    }

    private static bool IsEmbeddingModel(string tag)
        => tag.Contains("embed", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("all-minilm", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("bge-", StringComparison.OrdinalIgnoreCase);
}
