using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Runtimes.OpenAiCompat;
using Microsoft.Extensions.AI;
using OpenAI;

namespace IntoChat.OS.Modules.Devices;

internal sealed class OpenAiCompatRuntime : ILocalRuntime
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1200);
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private sealed record CachedSnapshot(RuntimeStatus Status, ModelCard[] Models, long CapturedAt);

    private readonly string _name;
    private readonly Uri _baseUrl;
    private readonly Uri _modelsUri;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedSnapshot? _cache;

    internal OpenAiCompatRuntime(string name, Uri baseUrl, HttpClient http, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(http);
        _name = name.Trim();
        _baseUrl = baseUrl;
        var root = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _modelsUri = new Uri(root, "models");
        _http = http;
        _clock = clock ?? TimeProvider.System;
        Id = RuntimeId.OpenAiCompat(_name);
    }

    public RuntimeId Id { get; }

    public async Task<RuntimeStatus> Probe(CancellationToken ct)
        => (await ReadSnapshotAsync(ct).ConfigureAwait(false)).Status;

    public IAsyncEnumerable<ModelCard> Catalog(CancellationToken ct) => Installed(ct);

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        var snapshot = await ReadSnapshotAsync(ct).ConfigureAwait(false);
        foreach (var card in snapshot.Models)
        {
            ct.ThrowIfCancellationRequested();
            yield return card;
        }
    }

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        InvalidateCache();
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        yield return new InstallProgress(id, InstallState.Failed, 0, 0, 0, NoDownloadsMessage);
    }

    public Task Remove(ModelId id, CancellationToken ct)
    {
        InvalidateCache();
        throw new NotSupportedException(NoDownloadsMessage);
    }

    public IChatClient Chat(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        var options = new OpenAIClientOptions { Endpoint = _baseUrl };
        return new OpenAIClient(new ApiKeyCredential("local"), options).GetChatClient(id.Value).AsIChatClient();
    }

    private void InvalidateCache() => Volatile.Write(ref _cache, null);

    private async Task<CachedSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _cache);
        if (cached is not null && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cache);
            if (cached is not null && _clock.GetElapsedTime(cached.CapturedAt) < CacheTtl)
            {
                return cached;
            }

            var snapshot = await FetchSnapshotAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _cache, snapshot);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CachedSnapshot> FetchSnapshotAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await _http.GetAsync(_modelsUri, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new CachedSnapshot(
                    new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: _baseUrl),
                    [],
                    _clock.GetTimestamp());
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var cards = ParseCards(body);
            return new CachedSnapshot(
                new RuntimeStatus(Id, Reachable: true, Version: null, Endpoint: _baseUrl),
                cards,
                _clock.GetTimestamp());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            OsConsoleLog.For<OpenAiCompatRuntime>().LogSwallowed(error, $"probing OpenAI-compatible runtime '{_name}' at {_modelsUri}");
            return new CachedSnapshot(
                new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: _baseUrl),
                [],
                _clock.GetTimestamp());
        }
    }

    private ModelCard[] ParseCards(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        ModelsResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, OpenAiCompatJson.Default.ModelsResponse);
        }
        catch (JsonException error)
        {
            OsConsoleLog.For<OpenAiCompatRuntime>().LogSwallowed(error, $"listing models from OpenAI-compatible runtime '{_name}' at {_modelsUri}");
            return [];
        }

        if (parsed?.Data is null || parsed.Data.Length == 0)
        {
            return [];
        }

        var cards = new List<ModelCard>(parsed.Data.Length);
        foreach (var entry in parsed.Data)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                continue;
            }

            if (KnownModels.Find(entry.Id) is { } known)
            {
                cards.Add(known);
                continue;
            }

            cards.Add(new ModelCard(
                Id: new ModelId(entry.Id),
                Provider: AiProvider.OpenAICompatible,
                Family: _name,
                Name: entry.Id,
                Where: Where.Device,
                Can: Abilities.Text | Abilities.Tools,
                Tier: Tier.Balanced));
        }

        return [.. cards];
    }

    private string NoDownloadsMessage => $"{_name} has no downloads; load the model in {_name}.";
}
