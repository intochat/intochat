using Microsoft.Extensions.AI;
using OllamaSharp;

namespace IntoChat.OS.Modules.AI.Models.Ollama;

internal sealed class OllamaProviderFactory : ILlmProviderFactory
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public AiProvider Provider => AiProvider.Ollama;

    public Task<bool> IsConfigured(AIOptions configuration, IAiCredentials credentials, CancellationToken cancellationToken = default)
        => Task.FromResult(!string.IsNullOrEmpty(configuration.Ollama.Endpoint));

    public Task<IChatClient> CreateChatClient(ModelCard card, AIOptions configuration, IAiCredentials credentials, CancellationToken cancellationToken = default)
        => CreateChatClient(ResolveConfiguredTag(configuration.Ollama, card), configuration, credentials, cancellationToken: cancellationToken);

    public Task<IChatClient> CreateChatClient(string model, AIOptions configuration, IAiCredentials credentials, string? endpoint = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IChatClient>(new ChatClientBuilder(CreateApiClient(configuration, model, model, "WithModel", useOverride: false, endpointOverride: endpoint))
            .ConfigureOptions(static options =>
            {
                options.AdditionalProperties ??= [];
                options.AdditionalProperties.TryAdd("think", false);
            })
            .Build());

    public Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGenerator(
        ModelCard card,
        AIOptions configuration,
        IAiCredentials credentials,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(
            CreateApiClient(configuration, card.Id.Value, ResolveConfiguredTag(configuration.Ollama, card), "WithModel", useOverride: false));

    internal static string? TryResolveConfiguredTag(OllamaOptions ollama, ModelCard card)
    {
        if (ollama.Models.TryGetValue(card.Id.Value, out var direct) && !string.IsNullOrWhiteSpace(direct.Model))
        {
            return direct.Model.Trim();
        }

        foreach (var (key, entry) in ollama.Models)
        {
            if (KnownModels.Find(key)?.Id == card.Id && !string.IsNullOrWhiteSpace(entry.Model))
            {
                return entry.Model.Trim();
            }
        }

        return null;
    }

    internal static string ResolveConfiguredTag(OllamaOptions ollama, ModelCard card)
        => TryResolveConfiguredTag(ollama, card) ?? card.Id.Value;

    private static string EndpointConfigurationKey => $"{AIClients.ConfigurationRoot}:Ollama:Endpoint";

    private static OllamaApiClient CreateApiClient(
        AIOptions configuration,
        string modelKey,
        string defaultTag,
        string hostingMethod,
        bool useOverride = true,
        string? endpointOverride = null)
    {
        var tag = useOverride ? configuration.Ollama.Models.GetValueOrDefault(modelKey)?.Model ?? defaultTag : defaultTag;
        var http = new HttpClient
        {
            BaseAddress = RequireEndpoint(configuration, modelKey, hostingMethod, endpointOverride),
            Timeout = RequestTimeout,
        };

        return new OllamaApiClient(http, tag);
    }

    private static Uri RequireEndpoint(AIOptions configuration, string modelKey, string hostingMethod, string? endpointOverride = null)
    {
        var endpoint = !string.IsNullOrWhiteSpace(endpointOverride) ? endpointOverride : configuration.Ollama.Endpoint;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            && (string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return endpointUri;
        }

        throw new NoProviderConfiguredException(
            $"{modelKey} requires {EndpointConfigurationKey} to be an absolute HTTP(S) URI. "
            + $"Configure it through AIOptions.{hostingMethod} in AppHost.");
    }
}
