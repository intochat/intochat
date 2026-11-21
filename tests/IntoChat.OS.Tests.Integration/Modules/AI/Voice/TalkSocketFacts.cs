#pragma warning disable MEAI001 // Integration tests for GET /ai/talk WebSocket endpoint.
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Voice;

public sealed class TalkSocketFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AudioGoesInAndAudioAndTextComeBack()
    {
        var realtime = new ScriptedRealtimeClient(onFirstAudio: session =>
        {
            session.Emit(new InputAudioTranscriptionRealtimeServerMessage(
                RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                Transcription = "hello",
            });
            session.Emit(new OutputTextAudioRealtimeServerMessage(
                RealtimeServerMessageType.OutputTextDelta)
            {
                ResponseId = "resp_1",
                Text = "Hi there.",
            });
            session.Emit(new OutputTextAudioRealtimeServerMessage(
                RealtimeServerMessageType.OutputAudioDelta)
            {
                ResponseId = "resp_1",
                Audio = Convert.ToBase64String(new byte[480]),
            });
            session.Emit(new ResponseCreatedRealtimeServerMessage(
                RealtimeServerMessageType.ResponseDone)
            {
                ResponseId = "resp_1",
                Status = RealtimeResponseStatus.Completed,
            });
        });

        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(realtime, KnownModels.GptRealtime.Id, RecordsItself: false, InputRate: 24_000, Unavailable: null)));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        var conversation = ConversationId.New();
        using var ws = await ConnectTalkAsync(host, conversation.Value, Ct);
        await ws.SendAsync(new byte[320], WebSocketMessageType.Binary, endOfMessage: true, Ct);

        var frames = await ReadUntilAsync(ws, static frame => frame is TalkState { State: "listening" }, Ct);

        Assert.Equal("hello", Assert.Single(frames.OfType<TalkHeard>()).Text);
        Assert.Equal("Hi there.", string.Concat(frames.OfType<TalkReply>().Select(static r => r.Text)));
        Assert.Contains(frames, static f => f is byte[] { Length: > 0 });
        Assert.Equal(320 * 24 / 16, realtime.ReceivedAudioBytes);

        await SendEventAsync(ws, new TalkEnd(), Ct);
    }

    [Fact]
    public async Task ACloudExchangeIsRecordedAsTwoSaids()
    {
        var realtime = new ScriptedRealtimeClient(onFirstAudio: session =>
        {
            session.Emit(new InputAudioTranscriptionRealtimeServerMessage(
                RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                Transcription = "what time is it",
            });
            session.Emit(new OutputTextAudioRealtimeServerMessage(
                RealtimeServerMessageType.OutputAudioTranscriptionDelta)
            {
                ResponseId = "resp_cloud_1",
                Text = "Ten past ",
            });
            session.Emit(new OutputTextAudioRealtimeServerMessage(
                RealtimeServerMessageType.OutputAudioTranscriptionDelta)
            {
                ResponseId = "resp_cloud_1",
                Text = "three.",
            });
            session.Emit(new OutputTextAudioRealtimeServerMessage(
                RealtimeServerMessageType.OutputAudioDelta)
            {
                ResponseId = "resp_cloud_1",
                Audio = Convert.ToBase64String(new byte[240]),
            });
            session.Emit(new ResponseCreatedRealtimeServerMessage(
                RealtimeServerMessageType.ResponseDone)
            {
                ResponseId = "resp_cloud_1",
                Status = RealtimeResponseStatus.Completed,
            });
        });

        string? recordedBrain = null;
        var stub = new StubVoiceClients(
            talk: (brain, _, _) =>
            {
                recordedBrain = brain;
                return Task.FromResult(new TalkVoice(realtime, KnownModels.GptRealtime.Id, RecordsItself: false, InputRate: 24_000, Unavailable: null));
            });

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        var conversation = ConversationId.New();
        using var ws = await ConnectTalkAsync(host, conversation.Value, Ct);
        await ws.SendAsync(new byte[320], WebSocketMessageType.Binary, endOfMessage: true, Ct);
        _ = await ReadUntilAsync(ws, static frame => frame is TalkState { State: "listening" }, Ct);
        await SendEventAsync(ws, new TalkEnd(), Ct);

        Assert.False(string.IsNullOrWhiteSpace(recordedBrain));
        var view = await ReadOwnerConversationAsync(host, recordedBrain!, conversation);
        var said = view.Entries.OfType<Said>().ToArray();
        Assert.Equal(["what time is it", "Ten past three."], said.Select(static s => s.Text));
        Assert.Equal(EntryIds.Heard("resp_cloud_1"), said[0].Id);
        Assert.Equal(EntryIds.Spoken("resp_cloud_1"), said[1].Id);
        Assert.IsType<Participant.Human>(said[0].By);
        Assert.IsType<Participant.Assistant>(said[1].By);
        Assert.Equal(KnownModels.GptRealtime.Id, said[1].Model);
    }

    [Fact]
    public async Task ALocalExchangeIsNotRecordedTwice()
    {
        var realtime = new ScriptedRealtimeClient(onFirstAudio: session =>
        {
            session.Emit(new InputAudioTranscriptionRealtimeServerMessage(
                RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                Transcription = "local hello",
            });
            session.Emit(new OutputTextAudioRealtimeServerMessage(
                RealtimeServerMessageType.OutputTextDelta)
            {
                ResponseId = "local_1",
                Text = "Local reply.",
            });
            session.Emit(new ResponseCreatedRealtimeServerMessage(
                RealtimeServerMessageType.ResponseDone)
            {
                ResponseId = "local_1",
                Status = RealtimeResponseStatus.Completed,
            });
        });

        string? recordedBrain = null;
        var stub = new StubVoiceClients(
            talk: (brain, _, _) =>
            {
                recordedBrain = brain;
                return Task.FromResult(new TalkVoice(realtime, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null));
            });

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        var conversation = ConversationId.New();
        using var ws = await ConnectTalkAsync(host, conversation.Value, Ct);
        await ws.SendAsync(new byte[320], WebSocketMessageType.Binary, endOfMessage: true, Ct);
        _ = await ReadUntilAsync(ws, static frame => frame is TalkState { State: "listening" }, Ct);
        await SendEventAsync(ws, new TalkEnd(), Ct);

        // Because RecordsItself is true and this stub realtime client does not call Say, TalkEndpoint must NOT call Voiced.
        Assert.False(string.IsNullOrWhiteSpace(recordedBrain));
        var view = await ReadOwnerConversationAsync(host, recordedBrain!, conversation);
        Assert.Empty(view.Entries.OfType<Said>());
    }

    [Fact]
    public async Task BargeInIsForwardedAsCancel()
    {
        var realtime = new ScriptedRealtimeClient(onFirstAudio: session =>
        {
            session.Emit(new RealtimeServerMessage { Type = LocalTalk.Interrupted });
        });

        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(realtime, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        var conversation = ConversationId.New();
        using var ws = await ConnectTalkAsync(host, conversation.Value, Ct);
        await ws.SendAsync(new byte[320], WebSocketMessageType.Binary, endOfMessage: true, Ct);

        var frames = await ReadUntilAsync(ws, static frame => frame is TalkCancel, Ct);
        Assert.Contains(frames, static f => f is TalkCancel);

        await SendEventAsync(ws, new TalkEnd(), Ct);
    }

    [Fact]
    public async Task ACrossSiteOriginIsRefused()
    {
        var realtime = new ScriptedRealtimeClient();
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(realtime, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        var conversation = ConversationId.New();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
        ws.Options.SetRequestHeader("Origin", "https://evil.example");
        ws.Options.CollectHttpResponseDetails = true;

        var error = await Assert.ThrowsAsync<WebSocketException>(() =>
            ws.ConnectAsync(TalkUri(host, conversation.Value), Ct));

        Assert.Equal(HttpStatusCode.Forbidden, ws.HttpStatusCode);
    }

    [Fact]
    public async Task AnAppCallerIsRefused()
    {
        var realtime = new ScriptedRealtimeClient();
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(realtime, Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));
        OverrideCallerAmbient? callerOverride = null;

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
            var existing = builder.Services.LastOrDefault(d => d.ServiceType == typeof(ICallerAmbient));
            if (existing is not null)
            {
                builder.Services.Remove(existing);
                builder.Services.AddSingleton<ICallerAmbient>(sp =>
                {
                    var inner = existing.ImplementationInstance as ICallerAmbient
                        ?? (existing.ImplementationFactory?.Invoke(sp) as ICallerAmbient)
                        ?? (ICallerAmbient)ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!);
                    callerOverride = new OverrideCallerAmbient(inner);
                    return callerOverride;
                });
            }
        });

        var edge = IntoChat.OS.Tests.TestEdge.Authority;
        var owner = new Actor.Person("owner", "owner");
        var brain = new BrainId("owner");
        Assert.NotNull(callerOverride);
        callerOverride!.Override = edge.App("some-app", owner, brain);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
        ws.Options.CollectHttpResponseDetails = true;

        _ = await Assert.ThrowsAsync<WebSocketException>(() =>
            ws.ConnectAsync(TalkUri(host, ConversationId.New().Value), Ct));

        Assert.Equal(HttpStatusCode.Forbidden, ws.HttpStatusCode);
    }

    [Fact]
    public async Task UnavailableTalkSaysWhyAndCloses()
    {
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(null, null, RecordsItself: true, InputRate: 16_000,
                "Talk needs Speak: Kokoro 82M is not installed.")));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        using var ws = await ConnectTalkAsync(host, ConversationId.New().Value, Ct);
        var (frames, closeStatus) = await ReadUntilClosedAsync(ws, Ct);

        var error = Assert.Single(frames.OfType<TalkError>());
        Assert.Contains("Kokoro 82M", error.Message, StringComparison.Ordinal);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, closeStatus);
    }

    private static Uri TalkUri(VoiceHostHarness host, string conversation)
    {
        var builder = new UriBuilder(host.BaseAddress)
        {
            Scheme = host.BaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ai/talk",
            Query = $"conversation={Uri.EscapeDataString(conversation)}",
        };
        return builder.Uri;
    }

    private static async Task<ClientWebSocket> ConnectTalkAsync(VoiceHostHarness host, string conversation, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
        await ws.ConnectAsync(TalkUri(host, conversation), ct);
        return ws;
    }

    private static Task SendEventAsync(ClientWebSocket ws, TalkEvent ev, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(ev, TalkJson.Default.TalkEvent);
        return ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<List<object>> ReadUntilAsync(ClientWebSocket ws, Func<object, bool> stop, CancellationToken ct)
    {
        var frames = new List<object>();
        var buffer = new byte[16 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        while (true)
        {
            var (type, payload) = await ReceiveMessageAsync(ws, buffer, timeout.Token);
            if (type == WebSocketMessageType.Close)
            {
                break;
            }

            object item = type == WebSocketMessageType.Binary
                ? payload
                : JsonSerializer.Deserialize(payload, TalkJson.Default.TalkEvent)!;
            frames.Add(item);
            if (stop(item))
            {
                break;
            }
        }

        return frames;
    }

    private static async Task<(List<object> Frames, WebSocketCloseStatus? CloseStatus)> ReadUntilClosedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var frames = new List<object>();
        var buffer = new byte[16 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        while (true)
        {
            var (type, payload) = await ReceiveMessageAsync(ws, buffer, timeout.Token);
            if (type == WebSocketMessageType.Close)
            {
                return (frames, ws.CloseStatus);
            }

            object item = type == WebSocketMessageType.Binary
                ? payload
                : JsonSerializer.Deserialize(payload, TalkJson.Default.TalkEvent)!;
            frames.Add(item);
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(
        ClientWebSocket ws,
        byte[] buffer,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, ms.ToArray());
            }
        }
    }

    private static async Task<ConversationView> ReadOwnerConversationAsync(VoiceHostHarness host, string brainId, ConversationId conversation)
    {
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        using var scope = edge.Enter(edge.Person("owner", "owner", new BrainId(brainId)));
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        var conv = proxies.Get<IConversation>(new NeuronId(new BrainId(brainId), IConversation.Type, IConversation.KeyOf(conversation)));
        return await conv.Read();
    }

    private sealed class ScriptedRealtimeClient(Action<ScriptedRealtimeSession>? onFirstAudio = null) : IRealtimeClient
    {
        private long _receivedAudioBytes;

        public long ReceivedAudioBytes => Interlocked.Read(ref _receivedAudioBytes);

        public Task<IRealtimeClientSession> CreateSessionAsync(
            RealtimeSessionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            IRealtimeClientSession session = new ScriptedRealtimeSession(this, options, onFirstAudio);
            return Task.FromResult(session);
        }

        internal void AddAudioBytes(int count) => Interlocked.Add(ref _receivedAudioBytes, count);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class ScriptedRealtimeSession(
        ScriptedRealtimeClient owner,
        RealtimeSessionOptions? options,
        Action<ScriptedRealtimeSession>? onFirstAudio) : IRealtimeClientSession
    {
        private readonly Channel<RealtimeServerMessage> _channel = Channel.CreateUnbounded<RealtimeServerMessage>();
        private int _triggered;

        public RealtimeSessionOptions? Options => options;

        public void Emit(RealtimeServerMessage message) => _channel.Writer.TryWrite(message);

        public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
        {
            if (message is InputAudioBufferAppendRealtimeClientMessage append)
            {
                owner.AddAudioBytes(append.Content.Data.Length);
                if (Interlocked.Exchange(ref _triggered, 1) == 0)
                {
                    onFirstAudio?.Invoke(this);
                }
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
