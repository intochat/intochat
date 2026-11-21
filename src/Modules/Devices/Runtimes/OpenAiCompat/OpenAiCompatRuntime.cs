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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly string _name;
    private readonly Uri _baseUrl;
    private readonly Uri _modelsUri;
    private readonly HttpClient _http;

    internal OpenAiCompatRuntime(string name, Uri baseUrl, HttpClient http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(http);
        _name = name.Trim();
        _baseUrl = baseUrl;
        var root = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _modelsUri = new Uri(root, "models");
        _http = http;
        Id = RuntimeId.OpenAiCompat(_name);
    }

    public RuntimeId Id { get; }

    public async Task<RuntimeStatus> Probe(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await _http.GetAsync(_modelsUri, linked.Token).ConfigureAwait(false);
            return new RuntimeStatus(Id, Reachable: response.IsSuccessStatusCode, Version: null, Endpoint: _baseUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            OsConsoleLog.For<OpenAiCompatRuntime>().LogSwallowed(error, $"probing OpenAI-compatible runtime '{_name}' at {_modelsUri}");
            return new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: _baseUrl);
        }
    }

    public IAsyncEnumerable<ModelCard> Catalog(CancellationToken ct) => Installed(ct);

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        ModelsResponse? parsed;
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await _http.GetAsync(_modelsUri, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                yield break;
            }
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            parsed = JsonSerializer.Deserialize(body, OpenAiCompatJson.Default.ModelsResponse);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            OsConsoleLog.For<OpenAiCompatRuntime>().LogSwallowed(error, $"listing models from OpenAI-compatible runtime '{_name}' at {_modelsUri}");
            yield break;
        }

        if (parsed?.Data is null)
        {
            yield break;
        }

        foreach (var entry in parsed.Data)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                continue;
            }

            if (KnownModels.Find(entry.Id) is { } known)
            {
                yield return known;
                continue;
            }

            yield return new ModelCard(
                Id: new ModelId(entry.Id),
                Provider: AiProvider.OpenAICompatible,
                Family: _name,
                Name: entry.Id,
                Where: Where.Device,
                Can: Abilities.Text | Abilities.Tools,
                Tier: Tier.Balanced);
        }
    }

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        yield return new InstallProgress(id, InstallState.Failed, 0, 0, 0, NoDownloadsMessage);
    }

    public Task Remove(ModelId id, CancellationToken ct)
        => throw new NotSupportedException(NoDownloadsMessage);

    public IChatClient Chat(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        var options = new OpenAIClientOptions { Endpoint = _baseUrl };
        return new OpenAIClient(new ApiKeyCredential("local"), options).GetChatClient(id.Value).AsIChatClient();
    }

    private string NoDownloadsMessage => $"{_name} has no downloads; load the model in {_name}.";
}
