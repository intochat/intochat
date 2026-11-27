using System.ClientModel;
using System.Collections.Immutable;
using DigitalBrain.Brains;
using IntoChat.OS.Modules.AI.Models.AzureFoundry;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace IntoChat.OS.Modules.AI.Models;

/// <summary>
/// Unified credential binding, provider factory invocation, and OTel/metering client construction
/// shared by both <see cref="ModelCatalog"/> (chat/embeddings) and <see cref="VoiceClients"/> (hear/speak/talk).
/// </summary>
internal sealed class ProviderClientBuilder
{
    private static readonly TimeSpan AudioTimeout = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider? _services;
    private readonly IOptionsMonitor<AIOptions>? _options;
    private readonly IAiCredentials? _credentials;
    private readonly IConfiguration? _configuration;
    private readonly ProviderPublicEndpoints? _publicEndpoints;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly AzureFoundryProviderFactory _foundryFactory;
    private readonly LocalRuntimeInventory _inventory;

    internal ProviderClientBuilder(
        IServiceProvider services,
        IOptionsMonitor<AIOptions> options,
        LocalRuntimeInventory inventory,
        AzureFoundryProviderFactory? foundryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inventory);

        _services = services;
        _options = options;
        _inventory = inventory;
        _foundryFactory = foundryFactory ?? (AzureFoundryProviderFactory)AIClients.Factory(AiProvider.AzureFoundry);
    }

    internal ProviderClientBuilder(
        IAiCredentials credentials,
        IConfiguration configuration,
        LocalRuntimeInventory inventory,
        ProviderPublicEndpoints? publicEndpoints = null,
        ILoggerFactory? loggerFactory = null,
        AzureFoundryProviderFactory? foundryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inventory);

        _credentials = credentials;
        _configuration = configuration;
        _inventory = inventory;
        _publicEndpoints = publicEndpoints;
        _loggerFactory = loggerFactory;
        _foundryFactory = foundryFactory ?? (AzureFoundryProviderFactory)AIClients.Factory(AiProvider.AzureFoundry);
    }

    private IAiCredentials DeploymentCredentials
        => _credentials ?? _services!.GetRequiredService<IAiCredentials>();

    private ILoggerFactory? LoggerFactory
        => _loggerFactory ?? _services?.GetService<ILoggerFactory>();

    private ProviderPublicEndpoints? PublicEndpoints
        => _publicEndpoints ?? _services?.GetService<ProviderPublicEndpoints>();

    internal bool HasHostChatClient
        => _services?.GetService<IChatClient>() is not null and not RebuildingChatClient;

    internal IAiCredentials CredentialsFor(string? brainId)
        => brainId is not null && BrainScope.IsValidId(brainId) && _services?.GetService<BrainKeyAvailability>() is { } keys
            ? new BrainAwareCredentials(DeploymentCredentials, keys, brainId)
            : DeploymentCredentials;

    internal async Task<ProviderReadiness> ReadReadinessAsync(
        AIOptions configuration,
        IAiCredentials credentials,
        CancellationToken ct)
    {
        var configured = ImmutableHashSet.CreateBuilder<AiProvider>();
        foreach (var provider in LocalRuntimeInventory.CloudChatProviders)
        {
            if (await AIClients.Factory(provider).IsConfigured(configuration, credentials, ct).ConfigureAwait(false))
            {
                configured.Add(provider);
            }
        }

        var typeSafeReady = await credentials.IsReady(AiIntegrations.TypeSafe.Id, ct).ConfigureAwait(false);
        return new ProviderReadiness(configured.ToImmutable(), typeSafeReady, HasHostChatClient);
    }

    internal async Task<IChatClient> CreateChatClientAsync(
        string? brainId,
        ResolvedModel resolved,
        CancellationToken ct,
        bool invokesTools = false)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ModelDecisionPolicy.ValidateRevision(resolved);

        var services = _services ?? throw new InvalidOperationException("Creating chat clients requires an IServiceProvider.");
        var credentials = CredentialsFor(brainId);
        _ = services.GetService<ProviderExecutionGate>()?.Bound(new ChatOptions { MaxOutputTokens = resolved.MaxOutputTokens });

        if (resolved.Provider == AiProvider.Host)
        {
            var hostClient = services.GetService<IChatClient>()
                ?? throw new NoProviderConfiguredException("No AI provider is configured.");
            return new NonDisposingChatClient(hostClient);
        }

        if (resolved.Provider == AiProvider.Device)
        {
            var (deviceClient, reason) = await _inventory.TryCreateDeviceRouteClientAsync(brainId, resolved, ct).ConfigureAwait(false);
            return deviceClient ?? throw new DeviceUnavailableException(reason ?? "offline");
        }

        IChatClient inner;
        if (LocalRuntimeInventory.IsDeviceProvider(resolved.Provider))
        {
            inner = await CreateDeviceChatClientAsync(resolved, ct).ConfigureAwait(false);
        }
        else
        {
            var configuration = new AIOptions();
            configuration.Provider(resolved.Provider).Endpoint = resolved.Endpoint;
            if (resolved.Provider == AiProvider.AzureFoundry)
            {
                configuration.AzureFoundry.UseEntraId = CurrentOptions().AzureFoundry.UseEntraId;
            }

            inner = new RebuildingChatClient(
                token => BuildCloudChatClientAsync(brainId, resolved, configuration, token),
                token => AiIntegrations.KeyedProviders.Contains(resolved.Provider)
                    ? credentials.GenerationOf(AiIntegrations.IdOf(resolved.Provider), token)
                    : Task.FromResult(0L));
        }

        var pinned = new ChatClientBuilder(inner).ConfigureOptions(request =>
        {
            request.ModelId = resolved.Model;
            if (resolved.Reasoning is not null)
            {
                request.Reasoning = ModelDecisionPolicy.ToReasoningOptions(resolved.Reasoning);
            }
            if (resolved.MaxOutputTokens is not null)
            {
                request.MaxOutputTokens = resolved.MaxOutputTokens;
            }
        }).Build();

        ct.ThrowIfCancellationRequested();
        return AIClients.BuildChatPipeline(
            services,
            !invokesTools || resolved.Capabilities.HasFlag(Abilities.Tools),
            resolved.Provider.ToString(),
            pinned,
            resolved.Provider.ToString(),
            resolved.Model,
            rejectUnsupportedTools: invokesTools,
            useFunctionInvocation: invokesTools);
    }

    internal Task<Voice<ISpeechToTextClient>> CreateSpeechToTextClientAsync(
        string brain,
        ModelCard card,
        CancellationToken ct)
        => CreateAudioVoiceClientAsync(
            brain,
            card,
            ModelRole.Hear,
            Abilities.Hear,
            verb: "hear",
            fromRuntime: static (runtime, id) => runtime.Hear(id),
            fromCloud: static (client, deployment) => client.GetAudioClient(deployment).AsISpeechToTextClient(),
            fromGoogle: static (modelId, apiKey) => new GeminiSpeechToTextClient(modelId, apiKey),
            wrapTelemetry: raw => raw.AsBuilder().UseOpenTelemetry(LoggerFactory, sourceName: VoiceClients.TelemetrySource).Build(),
            ct);

    internal Task<Voice<ITextToSpeechClient>> CreateTextToSpeechClientAsync(
        string brain,
        ModelCard card,
        CancellationToken ct)
        => CreateAudioVoiceClientAsync(
            brain,
            card,
            ModelRole.Speak,
            Abilities.Speak,
            verb: "speak",
            fromRuntime: static (runtime, id) => runtime.Speak(id),
            fromCloud: static (client, deployment) => client.GetAudioClient(deployment).AsITextToSpeechClient(),
            fromGoogle: static (modelId, apiKey) => new GeminiTextToSpeechClient(modelId, apiKey),
            wrapTelemetry: raw => raw.AsBuilder().UseOpenTelemetry(LoggerFactory, sourceName: VoiceClients.TelemetrySource).Build(),
            ct);

    internal async Task<TalkVoice> CreateRealtimeClientAsync(string brain, ModelCard card, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.Provider == AiProvider.Google)
        {
            var (googleKey, googleError) = await ResolveGoogleApiKeyAsync(brain, card, ModelRole.Talk, ct).ConfigureAwait(false);
            if (googleKey is null)
            {
                return new TalkVoice(null, card.Id, RecordsItself: false, InputRate: GeminiLiveRealtimeClient.InputSampleRate, googleError);
            }

            IRealtimeClient googleWrapped = new GeminiLiveRealtimeClient(card.Id.Value, googleKey)
                .AsBuilder()
                .UseOpenTelemetry(LoggerFactory, sourceName: VoiceClients.TelemetrySource)
                .Build();
            return new TalkVoice(googleWrapped, card.Id, RecordsItself: false, InputRate: GeminiLiveRealtimeClient.InputSampleRate, Unavailable: null);
        }

        if (card.Provider is not (AiProvider.OpenAI or AiProvider.AzureFoundry))
        {
            return new TalkVoice(null, card.Id, RecordsItself: false, InputRate: 24_000,
                $"{card.Name} cannot talk in this build.");
        }

        var (realtime, error) = await CreateCloudRealtimeClientAsync(brain, card, ct).ConfigureAwait(false);
        if (realtime is null)
        {
            return new TalkVoice(null, card.Id, RecordsItself: false, InputRate: 24_000, error);
        }

        IRealtimeClient wrapped = realtime
            .AsBuilder()
            .UseOpenTelemetry(LoggerFactory, sourceName: VoiceClients.TelemetrySource)
            .Build();
        return new TalkVoice(wrapped, card.Id, RecordsItself: false, InputRate: 24_000, Unavailable: null);
    }

    internal IRealtimeClient WrapLocalTalk(
        ISpeechToTextClient hear,
        IChatClient chat,
        ITextToSpeechClient speak,
        IVoiceActivityDetector vad,
        LocalTalkToolRouter? toolRouter = null)
        => new LocalTalk(hear, chat, speak, vad, toolRouter: toolRouter)
            .AsBuilder()
            .UseOpenTelemetry(LoggerFactory, sourceName: VoiceClients.TelemetrySource)
            .Build();

    private async Task<Voice<T>> CreateAudioVoiceClientAsync<T>(
        string brain,
        ModelCard card,
        ModelRole role,
        Abilities ability,
        string verb,
        Func<ILocalRuntime, ModelId, T?> fromRuntime,
        Func<OpenAIClient, string, T> fromCloud,
        Func<string, string, T> fromGoogle,
        Func<T, T> wrapTelemetry,
        CancellationToken ct) where T : class
    {
        if (!card.Can.HasFlag(ability))
        {
            return new Voice<T>(null, card.Id, $"{card.Name} cannot {verb} in this build.");
        }

        if (card.Where == Where.Device)
        {
            var logger = LoggerFactory?.CreateLogger<ProviderClientBuilder>();
            var runtime = await _inventory.FindInstalledRuntimeAsync(card.Id, logger, ct).ConfigureAwait(false);
            if (runtime is null)
            {
                return new Voice<T>(null, card.Id, $"{card.Name} is not installed for the {role} role.");
            }

            var raw = fromRuntime(runtime, card.Id);
            if (raw is null)
            {
                return new Voice<T>(null, card.Id, $"{card.Name} cannot {verb} in this build.");
            }

            return new Voice<T>(wrapTelemetry(raw), card.Id, Unavailable: null);
        }

        if (card.Provider == AiProvider.Google)
        {
            var (apiKey, googleError) = await ResolveGoogleApiKeyAsync(brain, card, role, ct).ConfigureAwait(false);
            if (apiKey is null)
            {
                return new Voice<T>(null, card.Id, googleError);
            }

            var raw = fromGoogle(card.Id.Value, apiKey);
            return new Voice<T>(wrapTelemetry(raw), card.Id, Unavailable: null);
        }

        if (card.Provider is AiProvider.OpenAI or AiProvider.AzureFoundry)
        {
            var (client, deployment, error) = await CreateCloudAudioOpenAIClientAsync(brain, card, role, ct).ConfigureAwait(false);
            if (client is null || deployment is null)
            {
                return new Voice<T>(null, card.Id, error);
            }

            var raw = fromCloud(client, deployment);
            return new Voice<T>(wrapTelemetry(raw), card.Id, Unavailable: null);
        }

        return new Voice<T>(null, card.Id, $"{card.Name} cannot {verb} in this build.");
    }

    private async Task<IChatClient> CreateDeviceChatClientAsync(ResolvedModel resolved, CancellationToken ct)
    {
        if (HasProfileEndpoint(resolved) && resolved.Endpoint is { Length: > 0 } profileEndpoint)
        {
            var uri = new Uri(profileEndpoint, UriKind.Absolute);
            return new OpenAIClient(new ApiKeyCredential("local"), new OpenAIClientOptions { Endpoint = uri })
                .GetChatClient(resolved.Model)
                .AsIChatClient();
        }

        var chosen = await _inventory.FindRuntimeForModelAsync(resolved.Provider, resolved.Model, ct).ConfigureAwait(false);
        if (chosen is null)
        {
            throw new NoProviderConfiguredException($"No local runtime is registered for provider '{resolved.Provider}' (model '{resolved.Model}').");
        }

        return chosen.Chat(new ModelId(resolved.Model));
    }

    private async Task<IChatClient> BuildCloudChatClientAsync(
        string? brainId,
        ResolvedModel resolved,
        AIOptions configuration,
        CancellationToken ct)
    {
        var factory = AIClients.Factory(resolved.Provider);
        var deployment = DeploymentCredentials;
        if (brainId is not null && BrainScope.IsValidId(brainId) && AiIntegrations.KeyedProviders.Contains(resolved.Provider)
            && !HasProfileEndpoint(resolved)
            && await deployment.BrainKey(brainId, resolved.Provider, ct).ConfigureAwait(false) is { Length: > 0 } key)
        {
            var publicEndpoint = resolved.Provider == AiProvider.AzureFoundry
                ? resolved.Endpoint ?? CurrentOptions().AzureFoundry.Endpoint
                : _services!.GetRequiredService<ProviderPublicEndpoints>().Of(resolved.Provider);
            return await factory.CreateChatClient(
                resolved.Model,
                new AIOptions(),
                new BrainKeyCredentials(AiIntegrations.IdOf(resolved.Provider), key),
                publicEndpoint,
                ct).ConfigureAwait(false);
        }

        return await factory.CreateChatClient(resolved.Model, configuration, deployment, resolved.Endpoint, ct).ConfigureAwait(false);
    }

    private async Task<(OpenAIClient? Client, string? Deployment, string? Error)> CreateCloudAudioOpenAIClientAsync(
        string brain,
        ModelCard card,
        ModelRole role,
        CancellationToken ct)
    {
        if (card.Provider == AiProvider.AzureFoundry)
        {
            try
            {
                var (endpoint, apiKey, policy) = await _foundryFactory.ResolveAuthAsync(
                    CurrentOptions(),
                    DeploymentCredentials,
                    pinnedEndpoint: null,
                    brainId: BrainScope.IsValidId(brain) ? brain : null,
                    ct).ConfigureAwait(false);
                var clientOptions = new OpenAIClientOptions { NetworkTimeout = AudioTimeout, Endpoint = endpoint };
#pragma warning disable OPENAI001 // OpenAIClient(AuthenticationPolicy, OpenAIClientOptions) for Entra ID BearerTokenPolicy.
                var client = apiKey is not null
                    ? new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
                    : new OpenAIClient(policy!, clientOptions);
#pragma warning restore OPENAI001
                return (client, AzureFoundryProviderFactory.DeploymentNameOf(card.Id.Value), null);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return (null, null, $"{card.Name} needs an Azure AI Foundry endpoint for the {role} role: {error.Message}");
            }
        }

        var (openAiKey, openAiEndpoint, openAiError) = await ResolveOpenAiCredentialsAsync(brain, card, role, ct).ConfigureAwait(false);
        if (openAiKey is null)
        {
            return (null, null, openAiError);
        }

        var options = new OpenAIClientOptions { NetworkTimeout = AudioTimeout };
        if (openAiEndpoint is { Length: > 0 })
        {
            options.Endpoint = new Uri(openAiEndpoint);
        }

        return (new OpenAIClient(new ApiKeyCredential(openAiKey), options), card.Id.Value, null);
    }

    private async Task<(OpenAIRealtimeClient? Client, string? Error)> CreateCloudRealtimeClientAsync(
        string brain,
        ModelCard card,
        CancellationToken ct)
    {
#pragma warning disable OPENAI001, OPENAI002 // OpenAI.Realtime client construction for OpenAI and Azure AI Foundry.
        if (card.Provider == AiProvider.AzureFoundry)
        {
            try
            {
                var (endpoint, apiKey, policy) = await _foundryFactory.ResolveAuthAsync(
                    CurrentOptions(),
                    DeploymentCredentials,
                    pinnedEndpoint: null,
                    brainId: BrainScope.IsValidId(brain) ? brain : null,
                    ct).ConfigureAwait(false);
                var realtimeOptions = new global::OpenAI.Realtime.RealtimeClientOptions
                {
                    Endpoint = AzureFoundryProviderFactory.RealtimeEndpointOf(endpoint),
                };
                var rawClient = apiKey is not null
                    ? new global::OpenAI.Realtime.RealtimeClient(new ApiKeyCredential(apiKey), realtimeOptions)
                    : new global::OpenAI.Realtime.RealtimeClient(policy!, realtimeOptions);
                return (new OpenAIRealtimeClient(rawClient, AzureFoundryProviderFactory.DeploymentNameOf(card.Id.Value)), null);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return (null, $"{card.Name} needs an Azure AI Foundry endpoint for the {ModelRole.Talk} role: {error.Message}");
            }
        }

        var (openAiKey, openAiEndpoint, openAiError) = await ResolveOpenAiCredentialsAsync(brain, card, ModelRole.Talk, ct).ConfigureAwait(false);
        if (openAiKey is null)
        {
            return (null, openAiError);
        }

        if (openAiEndpoint is { Length: > 0 }
            && Uri.TryCreate(openAiEndpoint, UriKind.Absolute, out var customUri)
            && !string.Equals(customUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            var realtimeOptions = new global::OpenAI.Realtime.RealtimeClientOptions
            {
                Endpoint = AzureFoundryProviderFactory.RealtimeEndpointOf(customUri),
            };
            var rawClient = new global::OpenAI.Realtime.RealtimeClient(new ApiKeyCredential(openAiKey), realtimeOptions);
            return (new OpenAIRealtimeClient(rawClient, card.Id.Value), null);
        }

        return (new OpenAIRealtimeClient(openAiKey, card.Id.Value), null);
#pragma warning restore OPENAI001, OPENAI002
    }

    private async Task<(string? ApiKey, string? Endpoint, string? Error)> ResolveOpenAiCredentialsAsync(
        string brain,
        ModelCard card,
        ModelRole role,
        CancellationToken ct)
    {
        var credentials = DeploymentCredentials;
        if (BrainScope.IsValidId(brain)
            && await credentials.BrainKey(brain, AiProvider.OpenAI, ct).ConfigureAwait(false) is { Length: > 0 } brainKey)
        {
            var endpoint = PublicEndpoints?.Of(AiProvider.OpenAI) ?? AiIntegrations.DefaultEndpointOf(AiProvider.OpenAI);
            return (brainKey, endpoint, null);
        }

        try
        {
            var material = await credentials.MaterializeAsync(AiIntegrations.IdOf(AiProvider.OpenAI), ct).ConfigureAwait(false);
            if (!material.Values.TryGetValue(AiIntegrations.ApiKeyField, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                return (null, null, $"{card.Name} needs an OpenAI API key for the {role} role.");
            }

            var endpoint = material.Snapshot.Settings.GetValueOrDefault(AiIntegrations.EndpointField);
            return (apiKey, endpoint, null);
        }
        catch (InvalidOperationException)
        {
            return (null, null, $"{card.Name} needs an OpenAI API key for the {role} role.");
        }
    }

    private async Task<(string? ApiKey, string? Error)> ResolveGoogleApiKeyAsync(
        string brain,
        ModelCard card,
        ModelRole role,
        CancellationToken ct)
    {
        var credentials = DeploymentCredentials;
        if (BrainScope.IsValidId(brain)
            && await credentials.BrainKey(brain, AiProvider.Google, ct).ConfigureAwait(false) is { Length: > 0 } brainKey)
        {
            return (brainKey, null);
        }

        try
        {
            var material = await credentials.MaterializeAsync(AiIntegrations.IdOf(AiProvider.Google), ct).ConfigureAwait(false);
            if (!material.Values.TryGetValue(AiIntegrations.ApiKeyField, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                return (null, $"{card.Name} needs a Google Gemini API key for the {role} role.");
            }

            return (apiKey, null);
        }
        catch (InvalidOperationException)
        {
            return (null, $"{card.Name} needs a Google Gemini API key for the {role} role.");
        }
    }

    private AIOptions CurrentOptions()
        => _options?.CurrentValue
            ?? (_configuration is not null ? AIOptionsRuntime.Read(_configuration) : new AIOptions());

    private bool HasProfileEndpoint(ResolvedModel resolved)
        => resolved.Profile is not null
            && ModelDecisionPolicy.Clean(CurrentOptions().ModelProfiles.GetValueOrDefault(resolved.Profile)?.Endpoint) is not null;

    private sealed class NonDisposingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
