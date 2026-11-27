using System.ClientModel;
using System.ClientModel.Primitives;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.AzureFoundry;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Voice;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed class VoiceClients : IVoiceClients
{
    internal const string TelemetrySource = "IntoChat.OS.Modules.AI.voice";
    private static readonly TimeSpan AudioTimeout = TimeSpan.FromMinutes(5);

    private readonly INeuronProxies _neurons;
    private readonly IEnumerable<ILocalRuntime> _runtimes;
    private readonly IAiCredentials _credentials;
    private readonly IConfiguration _configuration;
    private readonly IVoiceActivityDetector? _vad;
    private readonly ProviderPublicEndpoints? _publicEndpoints;
    private readonly Func<string, ConversationId, CancellationToken, Task<IChatClient?>> _chatFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly AzureFoundryProviderFactory _foundryFactory;

    public VoiceClients(
        INeuronProxies neurons,
        IEnumerable<ILocalRuntime> runtimes,
        IAiCredentials credentials,
        IConfiguration configuration,
        IServiceProvider services,
        ProviderPublicEndpoints? publicEndpoints = null,
        IVoiceActivityDetector? vad = null,
        ILoggerFactory? loggerFactory = null)
        : this(neurons, runtimes, credentials, configuration, vad, publicEndpoints, DefaultChatFactory(neurons, services), loggerFactory, foundryFactory: null)
    {
    }

    internal VoiceClients(
        INeuronProxies neurons,
        IEnumerable<ILocalRuntime> runtimes,
        IAiCredentials credentials,
        IConfiguration configuration,
        IVoiceActivityDetector? vad = null,
        ProviderPublicEndpoints? publicEndpoints = null,
        Func<string, ConversationId, CancellationToken, Task<IChatClient?>>? chatFactory = null,
        ILoggerFactory? loggerFactory = null,
        AzureFoundryProviderFactory? foundryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(configuration);

        _neurons = neurons;
        _runtimes = runtimes;
        _credentials = credentials;
        _configuration = configuration;
        _vad = vad;
        _publicEndpoints = publicEndpoints;
        _chatFactory = chatFactory ?? (static (_, _, _) => Task.FromResult<IChatClient?>(null));
        _loggerFactory = loggerFactory;
        _foundryFactory = foundryFactory ?? (AzureFoundryProviderFactory)AIClients.Factory(AiProvider.AzureFoundry);
    }

    private string? Topology => _configuration[IntoChatOSNames.TopologyKey];

    private ILogger Logger => _loggerFactory?.CreateLogger<VoiceClients>() ?? OsConsoleLog.For<VoiceClients>();

    public Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct)
        => ResolveVoiceAsync(
            brain,
            ModelRole.Hear,
            Abilities.Hear,
            verb: "hear",
            fromRuntime: static (runtime, id) => runtime.Hear(id),
            fromCloud: static (client, deployment) => client.GetAudioClient(deployment).AsISpeechToTextClient(),
            fromGoogle: static (modelId, apiKey) => new GeminiSpeechToTextClient(modelId, apiKey),
            wrapTelemetry: raw => raw.AsBuilder().UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource).Build(),
            ct);

    public Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct)
        => ResolveVoiceAsync(
            brain,
            ModelRole.Speak,
            Abilities.Speak,
            verb: "speak",
            fromRuntime: static (runtime, id) => runtime.Speak(id),
            fromCloud: static (client, deployment) => client.GetAudioClient(deployment).AsITextToSpeechClient(),
            fromGoogle: static (modelId, apiKey) => new GeminiTextToSpeechClient(modelId, apiKey),
            wrapTelemetry: raw => raw.AsBuilder().UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource).Build(),
            ct);

    public async Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct)
    {
        var catalog = CatalogFor(brain);
        var roles = await catalog.Roles().WaitAsync(ct).ConfigureAwait(false);
        var binding = RoleMaps.Deployment(roles, Topology, ModelRole.Talk).FirstOrDefault();

        if (binding is { Value.Length: > 0 })
        {
            var card = KnownModels.Find(binding.Value);
            if (card is null || !card.Can.HasFlag(Abilities.Talk))
            {
                return new TalkVoice(null, binding, RecordsItself: false, InputRate: 24_000,
                    $"{card?.Name ?? binding.Value} cannot talk in this build.");
            }

            if (card.Provider == AiProvider.Google)
            {
                var (googleKey, googleError) = await ResolveGoogleApiKeyAsync(brain, card, ModelRole.Talk, ct).ConfigureAwait(false);
                if (googleKey is null)
                {
                    return new TalkVoice(null, card.Id, RecordsItself: false, InputRate: GeminiLiveRealtimeClient.InputSampleRate, googleError);
                }

                IRealtimeClient googleWrapped = new GeminiLiveRealtimeClient(card.Id.Value, googleKey)
                    .AsBuilder()
                    .UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource)
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
                .UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource)
                .Build();
            return new TalkVoice(wrapped, card.Id, RecordsItself: false, InputRate: 24_000, Unavailable: null);
        }

        var hear = await Hear(brain, ct).ConfigureAwait(false);
        if (hear.Client is null)
        {
            return new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                $"Talk needs Hear: {hear.Unavailable ?? "no speech-to-text model is ready."}");
        }

        var speak = await Speak(brain, ct).ConfigureAwait(false);
        if (speak.Client is null)
        {
            hear.Client.Dispose();
            return new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                $"Talk needs Speak: {speak.Unavailable ?? "no text-to-speech model is ready."}");
        }

        var vad = _vad is { Ready: true } ? _vad : new EnergyVoiceActivityDetector();

        var chat = await _chatFactory(brain, conversation, ct).ConfigureAwait(false);
        if (chat is null)
        {
            hear.Client.Dispose();
            speak.Client.Dispose();
            return new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                "Talk needs Chat: no chat model is ready.");
        }

        IRealtimeClient local = new LocalTalk(hear.Client, chat, speak.Client, vad)
            .AsBuilder()
            .UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource)
            .Build();
        return new TalkVoice(local, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null);
    }

    private async Task<Voice<T>> ResolveVoiceAsync<T>(
        string brain,
        ModelRole role,
        Abilities ability,
        string verb,
        Func<ILocalRuntime, ModelId, T?> fromRuntime,
        Func<OpenAIClient, string, T> fromCloud,
        Func<string, string, T> fromGoogle,
        Func<T, T> wrapTelemetry,
        CancellationToken ct) where T : class
    {
        var catalog = CatalogFor(brain);
        ModelId picked;
        try
        {
            picked = await catalog.Pick(role).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            var reason = await DescribeMissingRoleAsync(catalog, role, ct).ConfigureAwait(false);
            return new Voice<T>(null, null, reason ?? error.Message);
        }

        var card = KnownModels.Find(picked.Value);
        if (card is null)
        {
            return new Voice<T>(null, picked, $"Model '{picked.Value}' cannot {verb} in this build.");
        }

        if (!card.Can.HasFlag(ability))
        {
            return new Voice<T>(null, card.Id, $"{card.Name} cannot {verb} in this build.");
        }

        if (card.Where == Where.Device)
        {
            var runtime = await FindInstalledRuntimeAsync(card.Id, ct).ConfigureAwait(false);
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

    private IModelCatalog CatalogFor(string brain)
    {
        var key = BrainScope.IsValidId(brain) ? IModelCatalog.Key(brain) : IModelCatalog.DeploymentKey;
        return _neurons.Get<IModelCatalog>(key);
    }

    private async Task<ILocalRuntime?> FindInstalledRuntimeAsync(ModelId id, CancellationToken ct)
    {
        foreach (var runtime in _runtimes)
        {
            try
            {
                await foreach (var installed in runtime.Installed(ct).ConfigureAwait(false))
                {
                    var canonical = KnownModels.Find(installed.Id.Value);
                    if (string.Equals(installed.Id.Value, id.Value, StringComparison.OrdinalIgnoreCase)
                        || (canonical is not null && canonical.Id == id))
                    {
                        return runtime;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Logger.LogSwallowed(error, $"checking installed models on runtime '{runtime.Id.Value}' for '{id.Value}'");
            }
        }

        return null;
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
                var aiOptions = AIOptionsRuntime.Read(_configuration);
                var (endpoint, apiKey, policy) = await _foundryFactory.ResolveAuthAsync(
                    aiOptions, _credentials, pinnedEndpoint: null, brainId: BrainScope.IsValidId(brain) ? brain : null, ct).ConfigureAwait(false);
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
                var aiOptions = AIOptionsRuntime.Read(_configuration);
                var (endpoint, apiKey, policy) = await _foundryFactory.ResolveAuthAsync(
                    aiOptions, _credentials, pinnedEndpoint: null, brainId: BrainScope.IsValidId(brain) ? brain : null, ct).ConfigureAwait(false);
                var realtimeOptions = new OpenAI.Realtime.RealtimeClientOptions
                {
                    Endpoint = AzureFoundryProviderFactory.RealtimeEndpointOf(endpoint),
                };
                var rawClient = apiKey is not null
                    ? new OpenAI.Realtime.RealtimeClient(new ApiKeyCredential(apiKey), realtimeOptions)
                    : new OpenAI.Realtime.RealtimeClient(policy!, realtimeOptions);
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
            var realtimeOptions = new OpenAI.Realtime.RealtimeClientOptions
            {
                Endpoint = AzureFoundryProviderFactory.RealtimeEndpointOf(customUri),
            };
            var rawClient = new OpenAI.Realtime.RealtimeClient(new ApiKeyCredential(openAiKey), realtimeOptions);
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
        if (BrainScope.IsValidId(brain)
            && await _credentials.BrainKey(brain, AiProvider.OpenAI, ct).ConfigureAwait(false) is { Length: > 0 } brainKey)
        {
            var endpoint = _publicEndpoints?.Of(AiProvider.OpenAI) ?? AiIntegrations.DefaultEndpointOf(AiProvider.OpenAI);
            return (brainKey, endpoint, null);
        }

        try
        {
            var material = await _credentials.MaterializeAsync(AiIntegrations.IdOf(AiProvider.OpenAI), ct).ConfigureAwait(false);
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
        if (BrainScope.IsValidId(brain)
            && await _credentials.BrainKey(brain, AiProvider.Google, ct).ConfigureAwait(false) is { Length: > 0 } brainKey)
        {
            return (brainKey, null);
        }

        try
        {
            var material = await _credentials.MaterializeAsync(AiIntegrations.IdOf(AiProvider.Google), ct).ConfigureAwait(false);
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

    private async Task<string?> DescribeMissingRoleAsync(IModelCatalog catalog, ModelRole role, CancellationToken ct)
    {
        try
        {
            var roles = await catalog.Roles().WaitAsync(ct).ConfigureAwait(false);
            var candidate = RoleMaps.Deployment(roles, Topology, role).FirstOrDefault();
            if (candidate is { Value.Length: > 0 })
            {
                var card = KnownModels.Find(candidate.Value);
                if (card is not null)
                {
                    return card.Where == Where.Device
                        ? $"{card.Name} is not installed for the {role} role."
                        : $"{card.Name} is not configured for the {role} role.";
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogSwallowed(error, $"describing missing {role} role model");
        }

        return null;
    }

    private static Func<string, ConversationId, CancellationToken, Task<IChatClient?>> DefaultChatFactory(
        INeuronProxies neurons,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentNullException.ThrowIfNull(services);
        return async (brain, conversation, ct) =>
        {
            var brains = services.GetService<IIntoChatOSBrains>();
            if (brains is not null && BrainScope.IsValidId(brain))
            {
                var conv = neurons.Get<IConversation>(new NeuronId(new BrainId(brain), IConversation.Type, IConversation.KeyOf(conversation)));
                var watch = brains.Open(brain);
                return new ConversationChatClient(conv, watch);
            }

            var catalog = services.GetService<ModelCatalog>();
            if (catalog is null)
            {
                return services.GetService<IChatClient>();
            }

            try
            {
                var resolved = await catalog.Resolve(brain, selection: null, requiresTools: false, ct).ConfigureAwait(false);
                return await catalog.CreateClient(brain, resolved, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        };
    }
}
