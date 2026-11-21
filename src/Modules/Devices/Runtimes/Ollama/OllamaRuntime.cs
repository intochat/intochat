using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Runtimes.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace IntoChat.OS.Modules.Devices;

public sealed class OllamaRuntime : ILocalRuntime
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<DevicesOptions> _options;
    private readonly TimeProvider _clock;

    internal OllamaRuntime(HttpClient http, IOptionsMonitor<DevicesOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _http = http;
        _options = options;
        _clock = clock;
    }

    public RuntimeId Id => RuntimeId.Ollama;

    private Uri Endpoint => DevicesOptions.ValidateHttpUrl(_options.CurrentValue.Ollama.Endpoint, "Ollama.Endpoint");

    public async Task<RuntimeStatus> Probe(CancellationToken ct)
    {
        var endpoint = Endpoint;
        using var timeout = new CancellationTokenSource(ProbeTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await _http.GetAsync(new Uri(endpoint, "/api/version"), linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: endpoint);
            }
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var parsed = string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize(body, OllamaJson.Default.VersionResponse);
            return new RuntimeStatus(Id, Reachable: true, Version: parsed?.Version, Endpoint: endpoint);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"probing Ollama at {endpoint}");
            return new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: endpoint);
        }
    }

    public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var card in KnownModels.All)
        {
            ct.ThrowIfCancellationRequested();
            if (card.Provider == AiProvider.Ollama)
            {
                yield return card;
            }
        }
    }

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        var endpoint = Endpoint;
        TagsResponse? tags;
        try
        {
            using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var response = await _http.GetAsync(new Uri(endpoint, "/api/tags"), linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                yield break;
            }
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            tags = JsonSerializer.Deserialize(body, OllamaJson.Default.TagsResponse);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"listing Ollama models from {endpoint}");
            yield break;
        }

        if (tags?.Models is null)
        {
            yield break;
        }

        foreach (var tag in tags.Models)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(tag.Name))
            {
                continue;
            }

            if (OllamaCards.FindCatalogued(tag.Name) is not null)
            {
                yield return OllamaCards.FromTag(tag, show: null);
                continue;
            }

            var show = await ReadShowAsync(endpoint, tag.Name, ct).ConfigureAwait(false);
            yield return OllamaCards.FromTag(tag, show);
        }
    }

    public async Task<IReadOnlyCollection<ModelId>> Loaded(CancellationToken ct)
    {
        var endpoint = Endpoint;
        try
        {
            using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var response = await _http.GetAsync(new Uri(endpoint, "/api/ps"), linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(body, OllamaJson.Default.PsResponse);
            return parsed?.Models is null
                ? []
                : [.. parsed.Models.Where(static m => !string.IsNullOrWhiteSpace(m.Name)).Select(static m => new ModelId(m.Name))];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"reading loaded Ollama models from {endpoint}");
            return [];
        }
    }

    public IAsyncEnumerable<InstallProgress> Install(ModelId id, CancellationToken ct)
        => PullAsync(_http, Endpoint, id, ct);

    public async Task Remove(ModelId id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        var endpoint = Endpoint;
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(endpoint, "/api/delete"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new DeleteRequest(id.Value), OllamaJson.Default.DeleteRequest),
                Encoding.UTF8,
                "application/json"),
        };
        using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public IChatClient Chat(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        return new OllamaApiClient(Endpoint, id.Value);
    }

    public IEmbeddingGenerator<string, Embedding<float>>? Embed(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        return new OllamaApiClient(Endpoint, id.Value);
    }

    public static async IAsyncEnumerable<InstallProgress> Pull(
        HttpMessageHandler handler,
        Uri endpoint,
        ModelId model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);

        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        await foreach (var progress in PullAsync(client, endpoint, model, ct).ConfigureAwait(false))
        {
            yield return progress;
        }
    }

    private static async IAsyncEnumerable<InstallProgress> PullAsync(
        HttpClient client,
        Uri endpoint,
        ModelId model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/api/pull"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new PullRequest(model.Value, Stream: true), OllamaJson.Default.PullRequest),
                Encoding.UTF8,
                "application/json"),
        };

        HttpResponseMessage? response = null;
        string? sendError = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            sendError = "Could not reach Ollama: " + error.Message;
        }

        if (sendError is not null)
        {
            yield return new InstallProgress(model, InstallState.Failed, 0, 0, 0, sendError);
            yield break;
        }

        using var httpResponse = response!;
        if (!httpResponse.IsSuccessStatusCode)
        {
            yield return new InstallProgress(model, InstallState.Failed, 0, 0, 0, $"Ollama answered {(int)httpResponse.StatusCode} to the pull request.");
            yield break;
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var layers = new Dictionary<string, (long Total, long Completed)>(StringComparer.Ordinal);
        double lastPercent = 0;
        long lastCompleted = 0;
        long lastTotal = 0;

        while (true)
        {
            string? rawLine = null;
            string? readError = null;
            try
            {
                rawLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                readError = "Could not reach Ollama: " + error.Message;
            }

            if (readError is not null)
            {
                yield return new InstallProgress(model, InstallState.Failed, lastPercent, lastCompleted, lastTotal, readError);
                yield break;
            }

            if (rawLine is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            PullLine? parsed = null;
            string? parseError = null;
            try
            {
                parsed = JsonSerializer.Deserialize(rawLine, OllamaJson.Default.PullLine);
            }
            catch (JsonException)
            {
                parseError = "Ollama sent a malformed progress line.";
            }

            if (parseError is not null)
            {
                yield return new InstallProgress(model, InstallState.Failed, lastPercent, lastCompleted, lastTotal, parseError);
                yield break;
            }

            if (parsed is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(parsed.Error))
            {
                yield return new InstallProgress(model, InstallState.Failed, lastPercent, lastCompleted, lastTotal, parsed.Error);
                yield break;
            }

            var status = parsed.Status ?? string.Empty;
            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                yield return new InstallProgress(model, InstallState.Ready, 100, lastTotal, lastTotal);
                yield break;
            }

            if (status.StartsWith("verifying", StringComparison.OrdinalIgnoreCase)
                || status.StartsWith("writing", StringComparison.OrdinalIgnoreCase))
            {
                lastPercent = 100;
                yield return new InstallProgress(model, InstallState.Verifying, 100, lastCompleted, lastTotal);
                continue;
            }

            if (status.StartsWith("pulling", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed.Total is > 0 and var layerTotal)
                {
                    var layerKey = !string.IsNullOrWhiteSpace(parsed.Digest) ? parsed.Digest : status;
                    var layerDone = Math.Clamp(parsed.Completed ?? 0, 0, layerTotal);
                    layers[layerKey] = (layerTotal, layerDone);
                }

                long sumTotal = 0;
                long sumCompleted = 0;
                foreach (var (t, c) in layers.Values)
                {
                    sumTotal += t;
                    sumCompleted += c;
                }

                lastTotal = sumTotal;
                lastCompleted = sumCompleted;
                lastPercent = sumTotal > 0 ? Math.Clamp(sumCompleted * 100.0 / sumTotal, 0, 100) : 0;
                yield return new InstallProgress(model, InstallState.Downloading, lastPercent, sumCompleted, sumTotal);
            }
        }

        yield return new InstallProgress(
            model,
            InstallState.Failed,
            lastPercent,
            lastCompleted,
            lastTotal,
            "The pull stream ended before Ollama reported success; it resumes on the next install.");
    }

    private async Task<ShowResponse?> ReadShowAsync(Uri endpoint, string modelName, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/api/show"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new ShowRequest(modelName), OllamaJson.Default.ShowRequest),
                    Encoding.UTF8,
                    "application/json"),
            };
            using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize(body, OllamaJson.Default.ShowResponse);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"reading Ollama model details for '{modelName}'");
            return null;
        }
    }
}
