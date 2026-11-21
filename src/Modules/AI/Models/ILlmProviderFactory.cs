using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Models;

internal interface ILlmProviderFactory
{
    AiProvider Provider { get; }

    Task<bool> IsConfigured(AIOptions configuration, IAiCredentials credentials, CancellationToken cancellationToken = default);

    Task<IChatClient> CreateChatClient(ModelCard card, AIOptions configuration, IAiCredentials credentials, CancellationToken cancellationToken = default);

    // The endpoint pins one client to a resolved model's endpoint; without it a hosted provider uses its registration.
    Task<IChatClient> CreateChatClient(string model, AIOptions configuration, IAiCredentials credentials, string? endpoint = null, CancellationToken cancellationToken = default);

    Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGenerator(
        ModelCard card,
        AIOptions configuration,
        IAiCredentials credentials,
        CancellationToken cancellationToken = default);
}
