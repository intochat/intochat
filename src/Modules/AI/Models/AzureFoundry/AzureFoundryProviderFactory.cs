using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using DigitalBrain.Capabilities.Connections.Integrations;
using Microsoft.Extensions.AI;
using OpenAI;

namespace IntoChat.OS.Modules.AI.Models.AzureFoundry;

internal sealed class AzureFoundryProviderFactory : ILlmProviderFactory
{
    internal const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";
    internal const string ModelIdPrefix = "foundry/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly Lazy<AuthenticationTokenProvider> SharedDefaultCredential = new(static () => new DefaultAzureCredential());
    private static readonly string[] TrustedEntraHostSuffixes =
    [
        ".services.ai.azure.com",
        ".openai.azure.com",
        ".cognitiveservices.azure.com",
    ];

    private readonly Func<AuthenticationTokenProvider> _tokenProviderFactory;

    public AzureFoundryProviderFactory()
        : this(static () => SharedDefaultCredential.Value)
    {
    }

    internal AzureFoundryProviderFactory(Func<AuthenticationTokenProvider> tokenProviderFactory)
    {
        ArgumentNullException.ThrowIfNull(tokenProviderFactory);
        _tokenProviderFactory = tokenProviderFactory;
    }

    public AiProvider Provider => AiProvider.AzureFoundry;

    private static string IntegrationId => AiIntegrations.IdOf(AiProvider.AzureFoundry);

    public async Task<bool> IsConfigured(AIOptions configuration, IAiCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentials);

        var snapshot = await credentials.StatusOf(IntegrationId, cancellationToken).ConfigureAwait(false);
        var rawEndpoint = snapshot.Settings.GetValueOrDefault(AiIntegrations.EndpointField)
            ?? configuration.AzureFoundry.Endpoint;
        if (!TryNormalizeEndpoint(rawEndpoint, out var normalized))
        {
            return false;
        }

        if (snapshot.Status == IntegrationStatus.Ready)
        {
            return true;
        }

        return configuration.AzureFoundry.UseEntraId && IsTrustedEntraHost(normalized);
    }

    public Task<IChatClient> CreateChatClient(ModelCard card, AIOptions configuration, IAiCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        return CreateChatClient(card.Id.Value, configuration, credentials, endpoint: null, cancellationToken);
    }

    public async Task<IChatClient> CreateChatClient(
        string model,
        AIOptions configuration,
        IAiCredentials credentials,
        string? endpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var deployment = DeploymentNameOf(model);
        var client = await CreateOpenAIClientAsync(configuration, credentials, endpoint, cancellationToken).ConfigureAwait(false);

        return new ChatClientBuilder(client.GetChatClient(deployment).AsIChatClient())
            .ConfigureOptions(static options =>
            {
                if (options.ModelId is { Length: > 0 })
                {
                    options.ModelId = DeploymentNameOf(options.ModelId);
                }
            })
            .UseStreamingUsage()
            .Build();
    }

    public async Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGenerator(
        ModelCard card,
        AIOptions configuration,
        IAiCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var client = await CreateOpenAIClientAsync(configuration, credentials, pinnedEndpoint: null, cancellationToken).ConfigureAwait(false);
        return client.GetEmbeddingClient(DeploymentNameOf(card.Id.Value)).AsIEmbeddingGenerator();
    }

    internal async Task<OpenAIClient> CreateOpenAIClientAsync(
        AIOptions configuration,
        IAiCredentials credentials,
        string? pinnedEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        var (endpoint, apiKey, policy) = await ResolveAuthAsync(configuration, credentials, pinnedEndpoint, brainId: null, cancellationToken).ConfigureAwait(false);
        var options = new OpenAIClientOptions { NetworkTimeout = RequestTimeout, Endpoint = endpoint };
#pragma warning disable OPENAI001 // OpenAIClient(AuthenticationPolicy, OpenAIClientOptions) for Entra ID BearerTokenPolicy.
        return apiKey is not null
            ? new OpenAIClient(new ApiKeyCredential(apiKey), options)
            : new OpenAIClient(policy!, options);
#pragma warning restore OPENAI001
    }

    internal async Task<(Uri Endpoint, string? ApiKey, AuthenticationPolicy? Policy)> ResolveAuthAsync(
        AIOptions configuration,
        IAiCredentials credentials,
        string? pinnedEndpoint,
        string? brainId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentials);

        string? explicitKey = null;
        string? integrationEndpoint = null;

        if (!string.IsNullOrWhiteSpace(brainId)
            && await credentials.BrainKey(brainId, AiProvider.AzureFoundry, cancellationToken).ConfigureAwait(false) is { Length: > 0 } brainKey)
        {
            explicitKey = brainKey;
        }
        else
        {
            var snapshot = await credentials.StatusOf(IntegrationId, cancellationToken).ConfigureAwait(false);
            integrationEndpoint = snapshot.Settings.GetValueOrDefault(AiIntegrations.EndpointField);
            if (snapshot.Status == IntegrationStatus.Ready)
            {
                var material = await credentials.MaterializeAsync(IntegrationId, cancellationToken).ConfigureAwait(false);
                integrationEndpoint = material.Snapshot.Settings.GetValueOrDefault(AiIntegrations.EndpointField) ?? integrationEndpoint;
                if (material.Values.TryGetValue(AiIntegrations.ApiKeyField, out var key) && !string.IsNullOrWhiteSpace(key))
                {
                    explicitKey = key;
                }
            }
        }

        var rawEndpoint = pinnedEndpoint is { Length: > 0 }
            ? pinnedEndpoint
            : integrationEndpoint is { Length: > 0 }
                ? integrationEndpoint
                : configuration.AzureFoundry.Endpoint;

        var endpoint = NormalizeEndpoint(rawEndpoint);

        if (explicitKey is { Length: > 0 })
        {
            return (endpoint, explicitKey, null);
        }

        if (configuration.AzureFoundry.UseEntraId)
        {
            if (!IsTrustedEntraHost(endpoint))
            {
                throw new ArgumentException(
                    $"Azure AI Foundry Entra ID authentication requires a trusted Azure AI Foundry or Cognitive Services host (*.services.ai.azure.com, *.openai.azure.com, *.cognitiveservices.azure.com) or loopback; '{endpoint.Host}' is not allowed.",
                    nameof(pinnedEndpoint));
            }

            var policy = new BearerTokenPolicy(_tokenProviderFactory(), CognitiveServicesScope);
            return (endpoint, null, policy);
        }

        throw new ProviderUnavailableException(IntegrationId, IntegrationStatus.Unconfigured.ToString(), [AiIntegrations.ApiKeyField]);
    }

    internal static string DeploymentNameOf(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var trimmed = modelId.Trim();
        return trimmed.StartsWith(ModelIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[ModelIdPrefix.Length..]
            : trimmed;
    }

    internal static bool TryNormalizeEndpoint(string? raw, out Uri normalized)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            normalized = NormalizeEndpoint(raw);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static Uri NormalizeEndpoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Azure AI Foundry endpoint is required.", nameof(raw));
        }

        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Azure AI Foundry endpoint must be an absolute URL without credentials, query strings, or fragments.", nameof(raw));
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            throw new ArgumentException("Azure AI Foundry endpoint must use https:// (http:// is allowed only for loopback).", nameof(raw));
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/openai/v1";
        }
        else if (path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1";
        }

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    internal static bool IsTrustedEntraHost(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.IsLoopback)
        {
            return true;
        }

        var host = endpoint.IdnHost;
        foreach (var suffix in TrustedEntraHostSuffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static Uri RealtimeEndpointOf(Uri openAiV1Endpoint)
    {
        ArgumentNullException.ThrowIfNull(openAiV1Endpoint);
        var scheme = string.Equals(openAiV1Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            ? "ws"
            : "wss";
        var path = openAiV1Endpoint.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/realtime", StringComparison.OrdinalIgnoreCase))
        {
            path += "/realtime";
        }

        var builder = new UriBuilder(openAiV1Endpoint)
        {
            Scheme = scheme,
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }
}
