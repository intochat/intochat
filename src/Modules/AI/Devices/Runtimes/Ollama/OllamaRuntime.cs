using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Runtimes.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;

namespace IntoChat.OS.Modules.Devices;

public sealed class OllamaRuntime : ILocalRuntime
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private sealed record CachedProbe(Uri Endpoint, RuntimeStatus Status, long CapturedAt);
    private sealed record CachedInstalled(Uri Endpoint, ModelCard[] Cards, long CapturedAt);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<DevicesOptions> _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly SemaphoreSlim _installedGate = new(1, 1);
    private CachedProbe? _cachedProbe;
    private CachedInstalled? _cachedInstalled;

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

    private TimeSpan ProbeTimeout => TimeSpan.FromMilliseconds(Math.Clamp(_options.CurrentValue.Ollama.ProbeTimeoutMs, 100, 60_000));

    private (OllamaApiClient Client, bool Dispose) CreateApiClient(Uri endpoint)
    {
        _http.BaseAddress ??= endpoint;

        return Uri.Equals(_http.BaseAddress, endpoint)
            ? (new OllamaApiClient(_http), false)
            : (new OllamaApiClient(endpoint), true);
    }

    private void InvalidateCache()
    {
        Volatile.Write(ref _cachedProbe, null);
        Volatile.Write(ref _cachedInstalled, null);
    }

    private bool IsKnownUnreachable(Uri endpoint)
        => Volatile.Read(ref _cachedProbe) is { } cached
            && Uri.Equals(cached.Endpoint, endpoint)
            && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl
            && !cached.Status.Reachable;

    public async Task<RuntimeStatus> Probe(CancellationToken ct)
    {
        var endpoint = Endpoint;
        var cached = Volatile.Read(ref _cachedProbe);
        if (cached is not null && Uri.Equals(cached.Endpoint, endpoint) && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl)
        {
            return cached.Status;
        }

        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cachedProbe);
            if (cached is not null && Uri.Equals(cached.Endpoint, endpoint) && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl)
            {
                return cached.Status;
            }

            var status = await FetchProbeAsync(endpoint, ct).ConfigureAwait(false);
            Volatile.Write(ref _cachedProbe, new CachedProbe(endpoint, status, _clock.GetTimestamp()));
            return status;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<RuntimeStatus> FetchProbeAsync(Uri endpoint, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var (client, dispose) = CreateApiClient(endpoint);
        try
        {
            var version = await client.GetVersionAsync(linked.Token).ConfigureAwait(false);
            return new RuntimeStatus(
                Id,
                Reachable: true,
                Version: string.IsNullOrWhiteSpace(version) ? null : version,
                Endpoint: endpoint);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or OllamaException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"probing Ollama at {endpoint}");
            return new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: endpoint);
        }
        finally
        {
            if (dispose)
            {
                client.Dispose();
            }
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
        var cards = await ReadInstalledAsync(ct).ConfigureAwait(false);
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            yield return card;
        }
    }

    private async Task<ModelCard[]> ReadInstalledAsync(CancellationToken ct)
    {
        var endpoint = Endpoint;
        var cached = Volatile.Read(ref _cachedInstalled);
        if (cached is not null && Uri.Equals(cached.Endpoint, endpoint) && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl)
        {
            return cached.Cards;
        }

        if (IsKnownUnreachable(endpoint))
        {
            return [];
        }

        await _installedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cachedInstalled);
            if (cached is not null && Uri.Equals(cached.Endpoint, endpoint) && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl)
            {
                return cached.Cards;
            }

            if (IsKnownUnreachable(endpoint))
            {
                return [];
            }

            var cards = await FetchInstalledAsync(endpoint, ct).ConfigureAwait(false);
            Volatile.Write(ref _cachedInstalled, new CachedInstalled(endpoint, cards, _clock.GetTimestamp()));
            return cards;
        }
        finally
        {
            _installedGate.Release();
        }
    }

    private async Task<ModelCard[]> FetchInstalledAsync(Uri endpoint, CancellationToken ct)
    {
        var (client, dispose) = CreateApiClient(endpoint);
        try
        {
            Model[]? models;
            try
            {
                using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                models = [.. await client.ListLocalModelsAsync(linked.Token).ConfigureAwait(false)];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or OllamaException)
            {
                OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"listing Ollama models from {endpoint}");
                return [];
            }

            if (models.Length == 0)
            {
                return [];
            }

            var cards = new List<ModelCard>(models.Length);
            foreach (var tag in models)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(tag.Name))
                {
                    continue;
                }

                if (OllamaCards.FindCatalogued(tag.Name) is not null)
                {
                    cards.Add(OllamaCards.FromTag(tag, show: null));
                    continue;
                }

                var show = await ReadShowAsync(client, tag.Name, ct).ConfigureAwait(false);
                cards.Add(OllamaCards.FromTag(tag, show));
            }

            return [.. cards];
        }
        finally
        {
            if (dispose)
            {
                client.Dispose();
            }
        }
    }

    public async Task<IReadOnlyCollection<ModelId>> Loaded(CancellationToken ct)
    {
        var endpoint = Endpoint;
        if (IsKnownUnreachable(endpoint))
        {
            return [];
        }

        var (client, dispose) = CreateApiClient(endpoint);
        try
        {
            using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var running = await client.ListRunningModelsAsync(linked.Token).ConfigureAwait(false);
            return running is null
                ? []
                : [.. running.Where(static m => !string.IsNullOrWhiteSpace(m.Name)).Select(static m => new ModelId(m.Name))];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or OllamaException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"reading loaded Ollama models from {endpoint}");
            return [];
        }
        finally
        {
            if (dispose)
            {
                client.Dispose();
            }
        }
    }

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        var (client, dispose) = CreateApiClient(Endpoint);
        try
        {
            await foreach (var progress in PullAsync(client, id, ct).ConfigureAwait(false))
            {
                if (progress.State is InstallState.Ready or InstallState.Failed or InstallState.Cancelled)
                {
                    InvalidateCache();
                }
                yield return progress;
            }
        }
        finally
        {
            if (dispose)
            {
                client.Dispose();
            }
            InvalidateCache();
        }
    }

    public async Task Remove(ModelId id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        var endpoint = Endpoint;
        using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var (client, dispose) = CreateApiClient(endpoint);
        try
        {
            await client.DeleteModelAsync(new DeleteModelRequest { Model = id.Value }, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (dispose)
            {
                client.Dispose();
            }
            InvalidateCache();
        }
    }

    public IChatClient Chat(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        IChatClient inner = new OllamaApiClient(Endpoint, id.Value);
        return new ChatClientBuilder(inner)
            .ConfigureOptions(static options =>
            {
                if (options.Reasoning?.Effort is { } effort && options.RawRepresentationFactory is null)
                {
                    var think = effort != ReasoningEffort.None;
                    options.RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = think };
                }
            })
            .Build();
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

        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OllamaApiClient(http);
        await foreach (var progress in PullAsync(client, model, ct).ConfigureAwait(false))
        {
            yield return progress;
        }
    }

    private static async IAsyncEnumerable<InstallProgress> PullAsync(
        OllamaApiClient client,
        ModelId model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var layers = new Dictionary<string, (long Total, long Completed)>(StringComparer.Ordinal);
        double lastPercent = 0;
        long lastCompleted = 0;
        long lastTotal = 0;

        await using var enumerator = client.PullModelAsync(
                new PullModelRequest { Model = model.Value, Stream = true },
                ct)
            .GetAsyncEnumerator(ct);

        while (true)
        {
            bool moved;
            string? streamError = null;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ResponseError error)
            {
                moved = false;
                streamError = error.Message;
            }
            catch (OllamaException error)
            {
                moved = false;
                streamError = error.Message;
            }
            catch (HttpRequestException error)
            {
                moved = false;
                streamError = error.StatusCode is { } code
                    ? $"Ollama answered {(int)code} to the pull request."
                    : "Could not reach Ollama: " + error.Message;
            }
            catch (JsonException)
            {
                moved = false;
                streamError = "Ollama sent a malformed progress line.";
            }

            if (streamError is not null)
            {
                yield return new InstallProgress(model, InstallState.Failed, lastPercent, lastCompleted, lastTotal, streamError);
                yield break;
            }

            if (!moved)
            {
                break;
            }

            var parsed = enumerator.Current;
            if (parsed is null)
            {
                continue;
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
                if (parsed.Total > 0)
                {
                    var layerKey = !string.IsNullOrWhiteSpace(parsed.Digest) ? parsed.Digest : status;
                    var layerDone = Math.Clamp(parsed.Completed, 0, parsed.Total);
                    layers[layerKey] = (parsed.Total, layerDone);
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

    private async Task<ShowModelResponse?> ReadShowAsync(OllamaApiClient client, string modelName, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(MetadataTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await client.ShowModelAsync(new ShowModelRequest { Model = modelName }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or OllamaException)
        {
            OsConsoleLog.For<OllamaRuntime>().LogSwallowed(error, $"reading Ollama model details for '{modelName}'");
            return null;
        }
    }
}
