#pragma warning disable MEAI001 // Unit tests for MEAI speech and realtime voice client resolution.
using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI.Audio;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Voice;

public sealed class VoiceClientsFacts
{
    private const string Brain = "local";
    private static readonly ConversationId Conversation = ConversationId.Parse("main");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable EnterPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person(Brain, Brain, new BrainId(Brain)));

    [Fact]
    public async Task DeviceSpeakComesFromTheRuntime()
    {
        var stub = new StubRuntime(
            id: RuntimeId.SherpaOnnx,
            installed: [KnownModels.Kokoro82M],
            speakClient: new StubSpeakClient());
        using var caller = EnterPerson();
        var (os, clients) = CreateClients(
            topology: IntoChatOSNames.TopologyLocal,
            runtimes: [stub],
            roles: KnownModels.Defaults,
            pickedSpeak: KnownModels.Kokoro82M.Id);
        await using var _ = os;

        var result = await clients.Speak(Brain, Ct);

        Assert.Null(result.Unavailable);
        Assert.Equal(KnownModels.Kokoro82M.Id, result.Model);
        Assert.NotNull(result.Client);
        Assert.Same(stub.SpeakClient, result.Client!.GetService<StubSpeakClient>());
    }

    [Fact]
    public async Task CloudHearIsOpenAiAudio()
    {
        using var caller = EnterPerson();
        var (os, clients) = CreateClients(
            topology: IntoChatOSNames.TopologyCloud,
            runtimes: [],
            roles: KnownModels.Defaults,
            pickedHear: KnownModels.Gpt4oTranscribe.Id,
            credentials: new FixedAiCredentials().Ready(AiIntegrations.IdOf(AiProvider.OpenAI), "sk-test-key"));
        await using var _ = os;

        var result = await clients.Hear(Brain, Ct);

        Assert.Null(result.Unavailable);
        Assert.Equal(KnownModels.Gpt4oTranscribe.Id, result.Model);
        Assert.NotNull(result.Client);
        Assert.NotNull(result.Client!.GetService<AudioClient>());
        Assert.Equal("openai", result.Client.GetService<SpeechToTextClientMetadata>()?.ProviderName);
    }

    [Fact]
    public async Task UnboundTalkOnTheDeviceIsLocalTalk()
    {
        var hearRuntime = new StubRuntime(
            id: RuntimeId.FoundryLocal,
            installed: [KnownModels.WhisperLargeV3Turbo],
            hearClient: new StubHearClient());
        var speakRuntime = new StubRuntime(
            id: RuntimeId.SherpaOnnx,
            installed: [KnownModels.Kokoro82M],
            speakClient: new StubSpeakClient());
        using var caller = EnterPerson();
        var (os, clients) = CreateClients(
            topology: IntoChatOSNames.TopologyLocal,
            runtimes: [hearRuntime, speakRuntime],
            roles: KnownModels.Defaults,
            pickedHear: KnownModels.WhisperLargeV3Turbo.Id,
            pickedSpeak: KnownModels.Kokoro82M.Id,
            vad: new StubVadDetector(ready: true),
            chatClient: new StubChatClient());
        await using var _ = os;

        var talk = await clients.Talk(Brain, Conversation, Ct);

        Assert.Null(talk.Unavailable);
        Assert.NotNull(talk.Client);
        Assert.IsType<LocalTalk>(talk.Client!.GetService<LocalTalk>());
        Assert.True(talk.RecordsItself);
        Assert.Equal(16_000, talk.InputRate);
    }

    [Fact]
    public async Task CloudTalkIsOpenAiRealtime()
    {
        using var caller = EnterPerson();
        var (os, clients) = CreateClients(
            topology: IntoChatOSNames.TopologyCloud,
            runtimes: [],
            roles: KnownModels.Defaults,
            credentials: new FixedAiCredentials().Ready(AiIntegrations.IdOf(AiProvider.OpenAI), "sk-test-key"));
        await using var _ = os;

        var talk = await clients.Talk(Brain, Conversation, Ct);

        Assert.Null(talk.Unavailable);
        Assert.NotNull(talk.Client);
        Assert.IsType<OpenAIRealtimeClient>(talk.Client!.GetService<OpenAIRealtimeClient>());
        Assert.False(talk.RecordsItself);
        Assert.Equal(KnownModels.GptRealtime.Id, talk.Model);
        Assert.Equal(24_000, talk.InputRate);
    }

    [Fact]
    public async Task TalkWithoutSpeakSaysWhy()
    {
        var hearRuntime = new StubRuntime(
            id: RuntimeId.FoundryLocal,
            installed: [KnownModels.WhisperLargeV3Turbo],
            hearClient: new StubHearClient());
        using var caller = EnterPerson();
        var (os, clients) = CreateClients(
            topology: IntoChatOSNames.TopologyLocal,
            runtimes: [hearRuntime],
            roles: KnownModels.Defaults,
            pickedHear: KnownModels.WhisperLargeV3Turbo.Id,
            pickedSpeak: KnownModels.Kokoro82M.Id,
            vad: new StubVadDetector(ready: true),
            chatClient: new StubChatClient());
        await using var _ = os;

        var talk = await clients.Talk(Brain, Conversation, Ct);

        Assert.Null(talk.Client);
        Assert.NotNull(talk.Unavailable);
        Assert.Contains("Speak", talk.Unavailable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResampleKeepsDuration()
        => Assert.Equal(24_000 * 2, Pcm16.Resample(new byte[16_000 * 2], 16_000, 24_000).Length);

    [Fact]
    public void WavHeaderNamesTheRate()
    {
        var wav = Pcm16.Wav(new byte[4], 24_000);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(24_000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(48, wav.Length);
    }

    private static (TestOS Os, VoiceClients Clients) CreateClients(
        string topology,
        IReadOnlyList<ILocalRuntime> runtimes,
        RoleMap roles,
        ModelId? pickedHear = null,
        ModelId? pickedSpeak = null,
        ModelId? pickedChat = null,
        IAiCredentials? credentials = null,
        IVoiceActivityDetector? vad = null,
        IChatClient? chatClient = null)
    {
        StubCatalogNeuron.ConfiguredRoles = roles;
        StubCatalogNeuron.Picked = new Dictionary<Role, ModelId?>
        {
            [Role.Hear] = pickedHear,
            [Role.Speak] = pickedSpeak,
            [Role.Chat] = pickedChat ?? KnownModels.Qwen35.Id,
        };

        var os = TestOS.Local().Register<StubCatalogNeuron>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = topology,
            })
            .Build();

        var clients = new VoiceClients(
            new TestOsNeuronProxies(os),
            runtimes,
            credentials ?? new FixedAiCredentials(),
            config,
            vad: vad,
            chatFactory: chatClient is null
                ? null
                : (_, _, _) => Task.FromResult<IChatClient?>(chatClient));

        return (os, clients);
    }

    [Neuron("ai.models"), StatelessNeuron]
    private sealed class StubCatalogNeuron : Neuron, IModelCatalog
    {
        public static RoleMap ConfiguredRoles { get; set; } = KnownModels.Defaults;
        public static Dictionary<Role, ModelId?> Picked { get; set; } = [];

        public Task<ModelCard[]> List(Abilities need = Abilities.None) => Task.FromResult(KnownModels.All.ToArray());

        public Task<ModelId> Pick(Role role, ModelId? turn = null)
        {
            if (turn is { Value.Length: > 0 } chosen)
            {
                return Task.FromResult(chosen);
            }

            if (Picked.TryGetValue(role, out var id) && id is { Value.Length: > 0 })
            {
                return Task.FromResult(id.Value);
            }

            throw new InvalidOperationException($"No model available for role '{role}'.");
        }

        public Task SetRole(Role role, ModelId model) => Task.CompletedTask;

        public Task<RoleMap> Roles() => Task.FromResult(ConfiguredRoles);

        public Task<ModelAvailability[]> Availability() => Task.FromResult<ModelAvailability[]>([]);

        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }

    private sealed class TestOsNeuronProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);

        public T Get<T>(string key) where T : class
        {
            var slash = key.IndexOf('/');
            var brain = new BrainId(slash > 0 ? key[..slash] : Brain);
            var localKey = slash > 0 ? key[(slash + 1)..] : key;
            var type = typeof(T) == typeof(IModelCatalog) ? "ai.models" : typeof(T).Name;
            return os.Get<T>(new NeuronId(brain, type, localKey));
        }
    }

    private sealed class StubRuntime(
        RuntimeId id,
        ModelCard[] installed,
        ISpeechToTextClient? hearClient = null,
        ITextToSpeechClient? speakClient = null) : ILocalRuntime
    {
        public RuntimeId Id => id;
        public ISpeechToTextClient? HearClient => hearClient;
        public ITextToSpeechClient? SpeakClient => speakClient;

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, true, "1.0", null));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var c in installed)
            {
                yield return c;
            }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var c in installed)
            {
                yield return c;
            }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId model, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId model, CancellationToken ct) => Task.CompletedTask;

        public IChatClient Chat(ModelId model) => throw new NotSupportedException();

        public ISpeechToTextClient? Hear(ModelId model) => hearClient;

        public ITextToSpeechClient? Speak(ModelId model) => speakClient;
    }

    private sealed class StubHearClient : ISpeechToTextClient
    {
        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpeechToTextResponse("hello"));

        public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class StubSpeakClient : ITextToSpeechClient
    {
        public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextToSpeechResponse([new DataContent(new byte[4], "audio/pcm;rate=24000")]));

        public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hi");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class StubVadDetector(bool ready) : IVoiceActivityDetector
    {
        public bool Ready => ready;

        public IVoiceActivity Open(int sampleRate = 16_000) => new NoopVad();

        private sealed class NoopVad : IVoiceActivity
        {
            public VoiceActivity Push(ReadOnlySpan<byte> pcm16) => new(false, TimeSpan.Zero, []);
            public void Dispose() { }
        }
    }
}
