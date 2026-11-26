using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.Devices;

[GenerateSerializer, Alias("devices.runtime-status")]
public sealed record RuntimeStatus(
    [property: Id(0)] RuntimeId Id,
    [property: Id(1)] bool Reachable,
    [property: Id(2)] string? Version,
    [property: Id(3)] Uri? Endpoint);

// Plain service, one per runtime; not a neuron.
public interface ILocalRuntime
{
    RuntimeId Id { get; }

    Task<RuntimeStatus> Probe(CancellationToken ct);

    IAsyncEnumerable<ModelCard> Catalog(CancellationToken ct);

    IAsyncEnumerable<ModelCard> Installed(CancellationToken ct);

    Task<IReadOnlyCollection<ModelId>> Loaded(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<ModelId>>([]);

    IAsyncEnumerable<InstallProgress> Install(ModelId id, CancellationToken ct);

    Task Remove(ModelId id, CancellationToken ct);

    IChatClient Chat(ModelId id);

    ISpeechToTextClient? Hear(ModelId id) => null;

    ITextToSpeechClient? Speak(ModelId id) => null;

    IEmbeddingGenerator<string, Embedding<float>>? Embed(ModelId id) => null;
}
