using System.ClientModel;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Voice;
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

    public VoiceClients(
        INeuronProxies neurons,
        IEnumerable<ILocalRuntime> runtimes,
        IAiCredentials credentials,
        IConfiguration configuration,
        IServiceProvider services,
        ProviderPublicEndpoints? publicEndpoints = null,
        IVoiceActivityDetector? vad = null,
        ILoggerFactory? loggerFactory = null)
        : this(neurons, runtimes, credentials, configuration, vad, publicEndpoints, DefaultChatFactory(services), loggerFactory)
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
        ILoggerFactory? loggerFactory = null)
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
    }

    private string? Topology => _configuration[IntoChatOSNames.TopologyKey];

    private ILogger Logger => _loggerFactory?.CreateLogger<VoiceClients>() ?? OsConsoleLog.For<VoiceClients>();

    public Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct)
        => ResolveVoiceAsync(
            brain,
            Role.Hear,
            Abilities.Hear,
            verb: "hear",
            fromRuntime: static (runtime, id) => runtime.Hear(id),
            fromOpenAi: static (apiKey, endpoint, id) => CreateAudioClient(apiKey, endpoint, id).AsISpeechToTextClient(),
            wrapTelemetry: raw => raw.AsBuilder().UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource).Build(),
            ct);

    public Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct)
        => ResolveVoiceAsync(
            brain,
            Role.Speak,
            Abilities.Speak,
            verb: "speak",
            fromRuntime: static (runtime, id) => runtime.Speak(id),
            fromOpenAi: static (apiKey, endpoint, id) => CreateAudioClient(apiKey, endpoint, id).AsITextToSpeechClient(),
            wrapTelemetry: raw => raw.AsBuilder().UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource).Build(),
            ct);

    public async Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct)
    {
        var catalog = CatalogFor(brain);
        var roles = await catalog.Roles().WaitAsync(ct).ConfigureAwait(false);
        var binding = RoleMaps.Deployment(roles, Topology, Role.Talk).FirstOrDefault();

        if (binding is { Value.Length: > 0 })
        {
            var card = KnownModels.Find(binding.Value);
            if (card is null || !card.Can.HasFlag(Abilities.Talk))
            {
                return new TalkVoice(null, binding, RecordsItself: false, InputRate: 24_000,
                    $"{card?.Name ?? binding.Value} cannot talk in this build.");
            }

            if (card.Provider != AiProvider.OpenAI)
            {
                return new TalkVoice(null, card.Id, RecordsItself: false, InputRate: 24_000,
                    $"{card.Name} cannot talk in this build.");
            }

            var (apiKey, _, error) = await ResolveOpenAiCredentialsAsync(brain, card, Role.Talk, ct).ConfigureAwait(false);
            if (apiKey is null)
            {
                return new TalkVoice(null, card.Id, RecordsItself: false, InputRate: 24_000, error);
            }

            IRealtimeClient realtime = new OpenAIRealtimeClient(apiKey, card.Id.Value)
                .AsBuilder()
                .UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource)
                .Build();
            return new TalkVoice(realtime, card.Id, RecordsItself: false, InputRate: 24_000, Unavailable: null);
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

        if (_vad is null || !_vad.Ready)
        {
            hear.Client.Dispose();
            speak.Client.Dispose();
            return new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                "Talk needs voice activity detection (Silero VAD is not installed).");
        }

        var chat = await _chatFactory(brain, conversation, ct).ConfigureAwait(false);
        if (chat is null)
        {
            hear.Client.Dispose();
            speak.Client.Dispose();
            return new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                "Talk needs Chat: no chat model is ready.");
        }

        IRealtimeClient local = new LocalTalk(hear.Client, chat, speak.Client, _vad)
            .AsBuilder()
            .UseOpenTelemetry(_loggerFactory, sourceName: TelemetrySource)
            .Build();
        return new TalkVoice(local, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null);
    }

    private async Task<Voice<T>> ResolveVoiceAsync<T>(
        string brain,
        Role role,
        Abilities ability,
        string verb,
        Func<ILocalRuntime, ModelId, T?> fromRuntime,
        Func<string, string?, ModelId, T> fromOpenAi,
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

        if (card.Provider == AiProvider.OpenAI)
        {
            var (apiKey, endpoint, error) = await ResolveOpenAiCredentialsAsync(brain, card, role, ct).ConfigureAwait(false);
            if (apiKey is null)
            {
                return new Voice<T>(null, card.Id, error);
            }

            var raw = fromOpenAi(apiKey, endpoint, card.Id);
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

    private async Task<(string? ApiKey, string? Endpoint, string? Error)> ResolveOpenAiCredentialsAsync(
        string brain,
        ModelCard card,
        Role role,
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

    private async Task<string?> DescribeMissingRoleAsync(IModelCatalog catalog, Role role, CancellationToken ct)
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

    private static OpenAI.Audio.AudioClient CreateAudioClient(string apiKey, string? endpoint, ModelId id)
    {
        var options = new OpenAIClientOptions { NetworkTimeout = AudioTimeout };
        if (endpoint is { Length: > 0 })
        {
            options.Endpoint = new Uri(endpoint);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetAudioClient(id.Value);
    }

    private static Func<string, ConversationId, CancellationToken, Task<IChatClient?>> DefaultChatFactory(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return async (brain, _, ct) =>
        {
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
