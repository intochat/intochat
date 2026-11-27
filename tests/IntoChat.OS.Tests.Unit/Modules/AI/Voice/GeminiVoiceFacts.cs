#pragma warning disable MEAI001 // Unit tests for Gemini voice clients, TalkTool wire protocol, and tool progress emission.
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Signals.Watch;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Voice;

public sealed class GeminiVoiceFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GeminiTtsUsesHeaderAuthWithoutQuerySecretAndReturnsPcm24k()
    {
        const string apiKey = "AIza-secret-tts-key";
        Uri? capturedUri = null;
        string? capturedHeader = null;
        string? capturedBody = null;
        var expectedPcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        using var handler = new StubHttpHandler(async (req, ct) =>
        {
            capturedUri = req.RequestUri;
            capturedHeader = req.Headers.TryGetValues("x-goog-api-key", out var vals) ? vals.FirstOrDefault() : null;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            var responseJson = $$"""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "inlineData": {
                              "mimeType": "audio/L16;codec=pcm;rate=24000",
                              "data": "{{Convert.ToBase64String(expectedPcm)}}"
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        using var http = new HttpClient(handler);
        using var client = new GeminiTextToSpeechClient(KnownModels.Gemini38FlashTts.Id.Value, apiKey, http);

        var response = await client.GetAudioAsync("Hello world", new TextToSpeechOptions { VoiceId = "Puck" }, Ct);

        Assert.NotNull(capturedUri);
        Assert.True(string.IsNullOrEmpty(capturedUri!.Query));
        Assert.DoesNotContain(apiKey, capturedUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(apiKey, capturedHeader);
        Assert.Contains("\"AUDIO\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"Puck\"", capturedBody, StringComparison.Ordinal);

        var audio = Assert.Single(response.Contents.OfType<DataContent>());
        Assert.Equal("audio/pcm;rate=24000", audio.MediaType);
        Assert.Equal(expectedPcm, audio.Data.ToArray());

        Assert.Equal("Kore", GeminiTextToSpeechClient.ResolveGeminiVoice("marin"));
        Assert.Equal("Fenrir", GeminiTextToSpeechClient.ResolveGeminiVoice("fenrir"));
    }

    [Fact]
    public async Task GeminiSttUsesHeaderAuthAndExtractsInlineAudioTranscript()
    {
        const string apiKey = "AIza-secret-stt-key";
        Uri? capturedUri = null;
        string? capturedHeader = null;
        string? capturedBody = null;

        using var handler = new StubHttpHandler(async (req, ct) =>
        {
            capturedUri = req.RequestUri;
            capturedHeader = req.Headers.TryGetValues("x-goog-api-key", out var vals) ? vals.FirstOrDefault() : null;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            const string responseJson = """
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "  Check the weather in Kyiv  " }
                        ]
                      }
                    }
                  ]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });

        using var http = new HttpClient(handler);
        using var client = new GeminiSpeechToTextClient(KnownModels.Gemini35Transcribe.Id.Value, apiKey, http);
        using var wav = new MemoryStream(Pcm16.Wav(new byte[320], 16_000));

        var response = await client.GetTextAsync(wav, cancellationToken: Ct);

        Assert.NotNull(capturedUri);
        Assert.True(string.IsNullOrEmpty(capturedUri!.Query));
        Assert.DoesNotContain(apiKey, capturedUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(apiKey, capturedHeader);
        Assert.Contains("\"audio/wav\"", capturedBody, StringComparison.Ordinal);
        Assert.Equal("Check the weather in Kyiv", response.Text);
        Assert.Equal(KnownModels.Gemini35Transcribe.Id.Value, response.ModelId);
    }

    [Fact]
    public async Task GeminiLiveUsesHeaderAuthHandlesBargeInAndRunBrainTurnToolCalls()
    {
        const string apiKey = "AIza-secret-live-key";
        Uri? capturedUri = null;
        IReadOnlyDictionary<string, string>? capturedHeaders = null;
        using var fakeSocket = new InMemoryWebSocket();

        using var client = new GeminiLiveRealtimeClient(
            KnownModels.Gemini38Live.Id.Value,
            apiKey,
            socketFactory: (uri, headers, _) =>
            {
                capturedUri = uri;
                capturedHeaders = headers;
                return Task.FromResult<WebSocket>(fakeSocket);
            });

        await using var session = await client.CreateSessionAsync(new RealtimeSessionOptions
        {
            Voice = "Aoede",
            Instructions = "Be concise.",
            Tools = [TalkEndpoint.CreateBrainTurnTool()],
        }, Ct);

        Assert.NotNull(capturedUri);
        Assert.True(string.IsNullOrEmpty(capturedUri!.Query));
        Assert.DoesNotContain(apiKey, capturedUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.NotNull(capturedHeaders);
        Assert.Equal(apiKey, capturedHeaders!["x-goog-api-key"]);

        var setupFrame = await fakeSocket.ReadSentTextAsync(Ct);
        Assert.Contains("models/gemini-3.8-live", setupFrame, StringComparison.Ordinal);
        Assert.Contains("\"Aoede\"", setupFrame, StringComparison.Ordinal);
        Assert.Contains(TalkEndpoint.BrainTurnToolName, setupFrame, StringComparison.Ordinal);

        await session.SendAsync(new InputAudioBufferAppendRealtimeClientMessage(new DataContent(new byte[64], "audio/pcm")), Ct);
        var audioFrame = await fakeSocket.ReadSentTextAsync(Ct);
        Assert.Contains("audio/pcm;rate=16000", audioFrame, StringComparison.Ordinal);

        fakeSocket.EnqueueServerText("""{"serverContent":{"interrupted":true}}""");
        fakeSocket.EnqueueServerText("""
            {
              "toolCall": {
                "functionCalls": [
                  {
                    "id": "call_gem_1",
                    "name": "run_brain_turn",
                    "args": { "request": "check memory" }
                  }
                ]
              }
            }
            """);

        var received = new List<RealtimeServerMessage>();
        await foreach (var msg in session.GetStreamingResponseAsync(Ct))
        {
            received.Add(msg);
            if (received.Count == 3)
            {
                break;
            }
        }

        Assert.Equal(LocalTalk.Interrupted, received[0].Type);
        var itemMsg = Assert.IsType<ResponseOutputItemRealtimeServerMessage>(received[1]);
        var call = Assert.Single(itemMsg.Item!.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_gem_1", call.CallId);
        Assert.Equal(TalkEndpoint.BrainTurnToolName, call.Name);
        Assert.Equal(RealtimeServerMessageType.ResponseDone, received[2].Type);

        await session.SendAsync(
            new CreateConversationItemRealtimeClientMessage(
                new RealtimeConversationItem([new FunctionResultContent("call_gem_1", "Memory checked.")])),
            Ct);
        var toolResponseFrame = await fakeSocket.ReadSentTextAsync(Ct);
        Assert.Contains("\"toolResponse\"", toolResponseFrame, StringComparison.Ordinal);
        Assert.Contains("\"call_gem_1\"", toolResponseFrame, StringComparison.Ordinal);
        Assert.Contains("Memory checked.", toolResponseFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void TalkToolWireJsonMatchesContract()
    {
        TalkEvent ev = new TalkTool("call_99", "web_search", "running", "Web search");
        var json = JsonSerializer.Serialize(ev, TalkJson.Default.TalkEvent);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tool", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("call_99", doc.RootElement.GetProperty("callId").GetString());
        Assert.Equal("web_search", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("running", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Web search", doc.RootElement.GetProperty("label").GetString());

        var roundTrip = Assert.IsType<TalkTool>(JsonSerializer.Deserialize(json, TalkJson.Default.TalkEvent));
        Assert.Equal(ev, roundTrip);
    }

    [Fact]
    public async Task ConversationChatClientEmitsTalkToolEventsForCalledAndReturned()
    {
        var conversation = new ScriptedToolConversation();
        var watch = new ScriptedSignalWatch();
        var emitted = new List<TalkTool>();

        using var chat = new ConversationChatClient(conversation, watch, (tool, _) =>
        {
            emitted.Add(tool);
            return Task.CompletedTask;
        });

        conversation.OnSay = () =>
        {
            var now = DateTimeOffset.UtcNow;
            var agent = new Participant.Assistant("ino");
            var run = new AgentRun("r1", AgentRunStatus.Running, "search", now);
            conversation.SetState(
                revision: 2,
                active: run,
                entries: [new Called(EntryIds.Call("r1", "c1"), agent, now, "web_search", "{}")]);
            watch.Emit(new ConversationChanged(ConversationId.Parse("main"), 2));

            conversation.SetState(
                revision: 3,
                active: run,
                entries: [new Returned(EntryIds.Result("r1", "c1"), agent, now, "web_search", "{\"ok\":true}", Ok: true, Card: null)]);
            watch.Emit(new ConversationChanged(ConversationId.Parse("main"), 3));

            conversation.SetState(
                revision: 4,
                active: null,
                entries: [new Said(EntryIds.Reply("r1"), agent, now, "Found it.")]);
            watch.Emit(new ConversationChanged(ConversationId.Parse("main"), 4));
        };

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "search")], cancellationToken: Ct);

        Assert.Equal("Found it.", response.Text);
        Assert.Equal(2, emitted.Count);
        Assert.Equal(new TalkTool("c1", "web_search", "running", "Web search"), emitted[0]);
        Assert.Equal(new TalkTool("c1", "web_search", "done", "Web search"), emitted[1]);
    }

    [Fact]
    public async Task GeminiLivePropagatesUpstreamInterruptFrameOnTapToInterrupt()
    {
        using var fakeSocket = new InMemoryWebSocket();
        using var client = new GeminiLiveRealtimeClient(
            KnownModels.Gemini38Live.Id.Value,
            "AIza-test-key",
            socketFactory: (_, _, _) => Task.FromResult<WebSocket>(fakeSocket));

        await using var session = await client.CreateSessionAsync(cancellationToken: Ct);
        _ = await fakeSocket.ReadSentTextAsync(Ct); // setup frame

#pragma warning disable OPENAI002
        await session.SendAsync(
            new RealtimeClientMessage { RawRepresentation = new OpenAI.Realtime.RealtimeClientCommandResponseCancel() },
            Ct);
#pragma warning restore OPENAI002

        var interruptFrame = await fakeSocket.ReadSentTextAsync(Ct);
        using var doc = JsonDocument.Parse(interruptFrame);
        Assert.True(doc.RootElement.TryGetProperty("clientContent", out var clientContent));
        Assert.True(clientContent.GetProperty("turnComplete").GetBoolean());

        await foreach (var msg in session.GetStreamingResponseAsync(Ct))
        {
            Assert.Equal(LocalTalk.Interrupted, msg.Type);
            break;
        }
    }

    [Fact]
    public void TalkTicketStoreEnforcesSingleUseStrict30sTtlAndConversationBinding()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var store = new TalkTicketStore(time);
        var caller = IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice-acct", new BrainId("alice"));
        var convA = ConversationId.Parse("main");
        var convB = ConversationId.Parse("other");

        var (ticket1, ttl) = store.Issue(caller, convA);
        Assert.Equal(30, ttl);
        Assert.True(ticket1.Length >= 32);

        // First redemption succeeds and returns the bound caller.
        Assert.True(store.TryRedeem(ticket1, convA, out var redeemed));
        Assert.Equal(caller.Brain, redeemed!.Brain);
        Assert.Equal(caller.For?.Principal, redeemed.For?.Principal);

        // Replay of the same ticket fails immediately (single-use).
        Assert.False(store.TryRedeem(ticket1, convA, out _));

        // Mismatched conversation fails and invalidates the ticket.
        var (ticket2, _) = store.Issue(caller, convA);
        Assert.False(store.TryRedeem(ticket2, convB, out _));
        Assert.False(store.TryRedeem(ticket2, convA, out _));

        // Expired after 30s fails.
        var (ticket3, _) = store.Issue(caller, convA);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(store.TryRedeem(ticket3, convA, out _));

        // Forbidden query-string bearer tokens are always rejected.
        Assert.True(TalkEndpoint.HasForbiddenQueryAuthToken(new QueryCollection(new Dictionary<string, StringValues> { ["access_token"] = "secret" })));
        Assert.True(TalkEndpoint.HasForbiddenQueryAuthToken(new QueryCollection(new Dictionary<string, StringValues> { ["token"] = "secret" })));
        Assert.True(TalkEndpoint.HasForbiddenQueryAuthToken(new QueryCollection(new Dictionary<string, StringValues> { ["bearer"] = "secret" })));
        Assert.False(TalkEndpoint.HasForbiddenQueryAuthToken(new QueryCollection(new Dictionary<string, StringValues> { ["ticket"] = ticket1, ["conversation"] = "main" })));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class InMemoryWebSocket : WebSocket
    {
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueServerText(string json) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

        public ValueTask<string> ReadSentTextAsync(CancellationToken ct) => _sent.Reader.ReadAsync(ct);

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var data = await _incoming.Reader.ReadAsync(cancellationToken);
            data.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(data.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _sent.Writer.TryWrite(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedSignalWatch : ISignalWatch
    {
        private readonly Channel<ConversationChanged> _changed = Channel.CreateUnbounded<ConversationChanged>();

        public void Emit(ConversationChanged signal) => _changed.Writer.TryWrite(signal);

        public async IAsyncEnumerable<TSignal> On<TSignal>(
            object target,
            Func<Task> onReady,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where TSignal : Signal
        {
            if (onReady is not null)
            {
                await onReady();
            }

            if (typeof(TSignal) == typeof(ConversationChanged))
            {
                await foreach (var item in _changed.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return (TSignal)(object)item;
                }

                yield break;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public IAsyncEnumerable<TSignal> On<TSignal>(
            object target,
            CancellationToken cancellationToken = default) where TSignal : Signal
            => On<TSignal>(target, static () => Task.CompletedTask, cancellationToken);
    }

    private sealed class ScriptedToolConversation : IConversation
    {
        private readonly Queue<(long Revision, AgentRun? Active, ConversationEntry[] Entries)> _snapshots = new();
        private long _revision;
        private AgentRun? _active;
        private ConversationEntry[] _entries = [];

        public Action? OnSay { get; set; }

        public void SetState(long revision, AgentRun? active, ConversationEntry[] entries)
        {
            _snapshots.Enqueue((revision, active, entries));
        }

        public Task<ConversationView> Read(long? since = null)
        {
            if (since is not null && _snapshots.Count > 0)
            {
                var next = _snapshots.Dequeue();
                _revision = next.Revision;
                _active = next.Active;
                _entries = next.Entries;
            }

            return Task.FromResult(new ConversationView(
                ConversationId.Parse("main"),
                _revision,
                [new Participant.Human(new AccountId("local")), new Participant.Assistant("ino")],
                _entries,
                _active,
                [],
                false,
                []));
        }

        public Task Say(string text, ModelId? model = null, AttachmentRef[]? attachments = null)
        {
            _revision = 1;
            OnSay?.Invoke();
            return Task.CompletedTask;
        }

        public Task Regenerate(EntryId entry) => Task.CompletedTask;
        public Task Invite(AccountId account) => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        public Task Seat(string agentId, AgentDefinition definition) => Task.CompletedTask;
        public Task Post(string text, string? id = null) => Task.CompletedTask;
        public Task Voiced(string heard, string spoken, ModelId model, string id) => Task.CompletedTask;
        public Task Leave() => Task.CompletedTask;
    }
}
