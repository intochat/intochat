using System.Runtime.CompilerServices;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Support.Fakes.Devices;

/// <summary>
/// First-class in-memory <see cref="ILocalRuntime"/> fake for unit, integration, and JourneyOS tests.
/// Replaces ad-hoc per-test runtime doubles with one reusable scriptable implementation.
/// </summary>
public sealed class FakeLocalRuntime : ILocalRuntime
{
    private readonly List<ModelCard> _catalog = [];
    private readonly List<ModelCard> _installed = [];
    private readonly HashSet<ModelId> _loaded = [];

    public FakeLocalRuntime(RuntimeId? id = null, bool reachable = true, Uri? endpoint = null)
    {
        Id = id ?? RuntimeId.Ollama;
        Reachable = reachable;
        Endpoint = endpoint ?? new Uri("http://127.0.0.1:11434");
    }

    public RuntimeId Id { get; }

    public bool Reachable { get; set; }

    public string Version { get; set; } = "fake-1.0";

    public Uri? Endpoint { get; set; }

    public IChatClient? ChatClient { get; set; }

    public ISpeechToTextClient? SpeechToTextClient { get; set; }

    public ITextToSpeechClient? TextToSpeechClient { get; set; }

    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; set; }

    public List<ModelId> InstalledHistory { get; } = [];

    public List<ModelId> RemovedHistory { get; } = [];

    public FakeLocalRuntime WithInstalled(params ModelCard[] cards)
    {
        foreach (var card in cards)
        {
            if (_installed.All(c => c.Id != card.Id))
            {
                _installed.Add(card);
            }
            if (_catalog.All(c => c.Id != card.Id))
            {
                _catalog.Add(card);
            }
        }
        return this;
    }

    public FakeLocalRuntime WithCatalog(params ModelCard[] cards)
    {
        foreach (var card in cards)
        {
            if (_catalog.All(c => c.Id != card.Id))
            {
                _catalog.Add(card);
            }
        }
        return this;
    }

    public FakeLocalRuntime WithLoaded(params ModelId[] ids)
    {
        foreach (var id in ids)
        {
            _loaded.Add(id);
        }
        return this;
    }

    public Task<RuntimeStatus> Probe(CancellationToken ct)
        => Task.FromResult(new RuntimeStatus(Id, Reachable, Reachable ? Version : null, Endpoint));

    public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var card in _catalog)
        {
            ct.ThrowIfCancellationRequested();
            yield return card;
        }
    }

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        if (!Reachable)
        {
            yield break;
        }
        foreach (var card in _installed)
        {
            ct.ThrowIfCancellationRequested();
            yield return card;
        }
    }

    public Task<IReadOnlyCollection<ModelId>> Loaded(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<ModelId>>([.. _loaded]);

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        InstalledHistory.Add(id);
        yield return new InstallProgress(id, InstallState.Downloading, 50, 50, 100);
        var card = _catalog.FirstOrDefault(c => c.Id == id) ?? KnownModels.Find(id.Value);
        if (card is not null && _installed.All(c => c.Id != card.Id))
        {
            _installed.Add(card);
        }
        yield return new InstallProgress(id, InstallState.Ready, 100, 100, 100);
    }

    public Task Remove(ModelId id, CancellationToken ct)
    {
        RemovedHistory.Add(id);
        _installed.RemoveAll(c => c.Id == id);
        _loaded.Remove(id);
        return Task.CompletedTask;
    }

    public IChatClient Chat(ModelId id)
        => ChatClient ?? throw new InvalidOperationException($"FakeLocalRuntime '{Id.Value}' has no ChatClient configured for '{id.Value}'.");

    public ISpeechToTextClient? Hear(ModelId id) => SpeechToTextClient;

    public ITextToSpeechClient? Speak(ModelId id) => TextToSpeechClient;

    public IEmbeddingGenerator<string, Embedding<float>>? Embed(ModelId id) => EmbeddingGenerator;
}
