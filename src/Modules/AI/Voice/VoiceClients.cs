using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
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

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed class VoiceClients : IVoiceClients
{
    internal const string TelemetrySource = "IntoChat.OS.Modules.AI.voice";

    private readonly INeuronProxies _neurons;
    private readonly IConfiguration _configuration;
    private readonly IVoiceActivityDetector? _vad;
    private readonly Func<string, ConversationId, Func<TalkTool, CancellationToken, Task>?, CancellationToken, Task<IChatClient?>> _chatFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ProviderClientBuilder _clientBuilder;

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
        : this(
            neurons,
            runtimes,
            credentials,
            configuration,
            vad,
            publicEndpoints,
            chatFactory is null ? null : (brain, conversation, _, ct) => chatFactory(brain, conversation, ct),
            loggerFactory,
            foundryFactory)
    {
    }

    internal VoiceClients(
        INeuronProxies neurons,
        IEnumerable<ILocalRuntime> runtimes,
        IAiCredentials credentials,
        IConfiguration configuration,
        IVoiceActivityDetector? vad,
        ProviderPublicEndpoints? publicEndpoints,
        Func<string, ConversationId, Func<TalkTool, CancellationToken, Task>?, CancellationToken, Task<IChatClient?>>? chatFactory,
        ILoggerFactory? loggerFactory = null,
        AzureFoundryProviderFactory? foundryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(configuration);

        _neurons = neurons;
        _configuration = configuration;
        _vad = vad;
        _chatFactory = chatFactory ?? (static (_, _, _, _) => Task.FromResult<IChatClient?>(null));
        _loggerFactory = loggerFactory;

        var inventory = new LocalRuntimeInventory(runtimes);
        _clientBuilder = new ProviderClientBuilder(
            credentials,
            configuration,
            inventory,
            publicEndpoints,
            loggerFactory,
            foundryFactory);
    }

    private string? Topology => _configuration[IntoChatOSNames.TopologyKey];

    private ILogger Logger => _loggerFactory?.CreateLogger<VoiceClients>() ?? OsConsoleLog.For<VoiceClients>();

    public Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct)
        => ResolveVoiceAsync(
            brain,
            ModelRole.Hear,
            verb: "hear",
            createClient: _clientBuilder.CreateSpeechToTextClientAsync,
            ct);

    public Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct)
        => ResolveVoiceAsync(
            brain,
            ModelRole.Speak,
            verb: "speak",
            createClient: _clientBuilder.CreateTextToSpeechClientAsync,
            ct);

    public async Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct)
    {
        var catalog = CatalogFor(brain);
        var roles = await catalog.Roles().WaitAsync(ct).ConfigureAwait(false);
        var selection = ModelDecisionPolicy.SelectTalkBinding(roles, Topology);

        if (selection.ModelId is { Value.Length: > 0 })
        {
            if (selection.UnavailableReason is not null || selection.Card is null)
            {
                return new TalkVoice(null, selection.ModelId, RecordsItself: false, InputRate: 24_000,
                    selection.UnavailableReason ?? $"{selection.ModelId.Value.Value} cannot talk in this build.");
            }

            return await _clientBuilder.CreateRealtimeClientAsync(brain, selection.Card, ct).ConfigureAwait(false);
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
        var toolRouter = new LocalTalkToolRouter();

        var chat = await _chatFactory(brain, conversation, toolRouter.EmitAsync, ct).ConfigureAwait(false);
        if (chat is null)
        {
            hear.Client.Dispose();
            speak.Client.Dispose();
            return new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                "Talk needs Chat: no chat model is ready.");
        }

        var local = _clientBuilder.WrapLocalTalk(hear.Client, chat, speak.Client, vad, toolRouter);
        return new TalkVoice(local, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null);
    }

    private async Task<Voice<T>> ResolveVoiceAsync<T>(
        string brain,
        ModelRole role,
        string verb,
        Func<string, ModelCard, CancellationToken, Task<Voice<T>>> createClient,
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

        return await createClient(brain, card, ct).ConfigureAwait(false);
    }

    private IModelCatalog CatalogFor(string brain)
    {
        var key = BrainScope.IsValidId(brain) ? IModelCatalog.Key(brain) : IModelCatalog.DeploymentKey;
        return _neurons.Get<IModelCatalog>(key);
    }

    private async Task<string?> DescribeMissingRoleAsync(IModelCatalog catalog, ModelRole role, CancellationToken ct)
    {
        try
        {
            var roles = await catalog.Roles().WaitAsync(ct).ConfigureAwait(false);
            return ModelDecisionPolicy.DescribeMissingVoiceRole(roles, Topology, role);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogSwallowed(error, $"describing missing {role} role model");
            return null;
        }
    }

    internal static Func<string, ConversationId, Func<TalkTool, CancellationToken, Task>?, CancellationToken, Task<IChatClient?>> DefaultChatFactory(
        INeuronProxies neurons,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentNullException.ThrowIfNull(services);
        return async (brain, conversation, onToolEvent, ct) =>
        {
            var brains = services.GetService<IIntoChatOSBrains>();
            if (brains is not null && BrainScope.IsValidId(brain))
            {
                var conv = neurons.Get<IConversation>(new NeuronId(new BrainId(brain), IConversation.Type, IConversation.KeyOf(conversation)));
                var watch = brains.Open(brain);
                return new ConversationChatClient(conv, watch, onToolEvent);
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
