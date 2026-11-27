#pragma warning disable MEAI001 // Integration tests for /ai/talk WebSocket authentication gates.
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Voice;

[Collection("VoiceHost")]
public sealed class TalkSocketAuthFacts(VoiceHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TicketSingleUseReplayAndConversationMismatchAreRejected()
    {
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(new NoopRealtimeClient(), Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));

        using var clients = fixture.UseClients(stub);
        var host = fixture.Host;
        var convA = ConversationId.New();
        var convB = ConversationId.New();

        // Mint ticket for convA.
        var ticket = await MintTicketAsync(host, convA.Value);

        // First upgrade for convA succeeds without Authorization/Presence headers.
        var uriA = TalkUri(host, convA.Value, ticket: ticket);
        using (var ws = new ClientWebSocket())
        {
            await ws.ConnectAsync(uriA, Ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        }

        // Replay of the same ticket fails with 401.
        using (var replayWs = new ClientWebSocket())
        {
            replayWs.Options.CollectHttpResponseDetails = true;
            _ = await Assert.ThrowsAsync<WebSocketException>(() => replayWs.ConnectAsync(uriA, Ct));
            Assert.Equal(HttpStatusCode.Unauthorized, replayWs.HttpStatusCode);
        }

        // Ticket minted for convA cannot be used for convB (401) and is invalidated immediately.
        var ticket2 = await MintTicketAsync(host, convA.Value);
        using (var wrongConvWs = new ClientWebSocket())
        {
            wrongConvWs.Options.CollectHttpResponseDetails = true;
            _ = await Assert.ThrowsAsync<WebSocketException>(() =>
                wrongConvWs.ConnectAsync(TalkUri(host, convB.Value, ticket: ticket2), Ct));
            Assert.Equal(HttpStatusCode.Unauthorized, wrongConvWs.HttpStatusCode);
        }

        // Even retrying convA after the failed convB attempt is rejected (single-use atomic removal).
        using var retryWs = new ClientWebSocket();
        retryWs.Options.CollectHttpResponseDetails = true;
        _ = await Assert.ThrowsAsync<WebSocketException>(() =>
            retryWs.ConnectAsync(TalkUri(host, convA.Value, ticket: ticket2), Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, retryWs.HttpStatusCode);
    }

    [Fact]
    public async Task CrossBrainTicketUpgradeWithDifferentStampedPrincipalIsRejected()
    {
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(new NoopRealtimeClient(), Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));

        using var clients = fixture.UseClients(stub);
        var host = fixture.Host;
        var conv = ConversationId.New();

        // Mint ticket as Brain A (default owner).
        var ticketForA = await MintTicketAsync(host, conv.Value);

        // Upgrade while stamped as Brain B must be rejected with 403.
        var edge = IntoChat.OS.Tests.TestEdge.Authority;
        using var callerB = fixture.OverrideCaller(edge.Person("bob", "bob", new BrainId("bob")));
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        _ = await Assert.ThrowsAsync<WebSocketException>(() =>
            ws.ConnectAsync(TalkUri(host, conv.Value, ticket: ticketForA), Ct));
        Assert.Equal(HttpStatusCode.Forbidden, ws.HttpStatusCode);
    }

    [Fact]
    public async Task DisallowedOriginAndQueryStringBearerTokensAreRejected()
    {
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(new NoopRealtimeClient(), Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));

        using var clients = fixture.UseClients(stub);
        var host = fixture.Host;
        var conv = ConversationId.New();

        // Disallowed Origin -> 403 even with valid Presence header.
        using (var evilOriginWs = new ClientWebSocket())
        {
            evilOriginWs.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
            evilOriginWs.Options.SetRequestHeader("Origin", "https://attacker.invalid");
            evilOriginWs.Options.CollectHttpResponseDetails = true;
            _ = await Assert.ThrowsAsync<WebSocketException>(() =>
                evilOriginWs.ConnectAsync(TalkUri(host, conv.Value), Ct));
            Assert.Equal(HttpStatusCode.Forbidden, evilOriginWs.HttpStatusCode);
        }

        // Query-string bearer/API keys -> 401 across all forbidden parameter names.
        foreach (var param in new[] { "access_token", "token", "authorization", "bearer" })
        {
            using var qsWs = new ClientWebSocket();
            qsWs.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
            qsWs.Options.CollectHttpResponseDetails = true;
            var uri = new UriBuilder(TalkUri(host, conv.Value))
            {
                Query = $"conversation={Uri.EscapeDataString(conv.Value)}&{param}=secret-bearer-token",
            }.Uri;
            _ = await Assert.ThrowsAsync<WebSocketException>(() => qsWs.ConnectAsync(uri, Ct));
            Assert.Equal(HttpStatusCode.Unauthorized, qsWs.HttpStatusCode);
        }
    }

    [Fact]
    public async Task HttpOnlyPresenceCookieAuthenticatesWebSocketUpgrade()
    {
        var stub = new StubVoiceClients(
            talk: (_, _, _) => Task.FromResult(new TalkVoice(new NoopRealtimeClient(), Model: null, RecordsItself: true, InputRate: 16_000, Unavailable: null)));

        using var clients = fixture.UseClients(stub);
        var host = fixture.Host;
        var conv = ConversationId.New();

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Cookie", $"{PresenceProof.CookieName}={host.PresenceToken}");
        await ws.ConnectAsync(TalkUri(host, conv.Value), Ct);
        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Fact]
    public void TicketStoreEnforces30SecondTtlAndPerPrincipalCap()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero));
        var store = new TalkTicketStore(clock);
        var callerA = IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId("alice"));
        var callerB = IntoChat.OS.Tests.TestEdge.Authority.Person("bob", "bob", new BrainId("bob"));
        var conv = ConversationId.Parse("main");

        // Ticket expires strictly at 30s.
        var (expiring, ttl) = store.Issue(callerA, conv);
        Assert.Equal(30, ttl);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(store.TryRedeem(expiring, conv, out _));

        // Per-principal cap prevents one user from exhausting the global store.
        for (var i = 0; i < TalkTicketStore.MaxTicketsPerPrincipal; i++)
        {
            _ = store.Issue(callerA, conv);
        }

        Assert.Throws<InvalidOperationException>(() => store.Issue(callerA, conv));

        // Another principal is unaffected.
        var (bobTicket, _) = store.Issue(callerB, conv);
        Assert.True(store.TryRedeem(bobTicket, conv, out var redeemedBob));
        Assert.Equal(callerB.Brain, redeemedBob!.Brain);
    }

    private static async Task<string> MintTicketAsync(VoiceHostHarness host, string conversation)
    {
        using var response = await host.PostJsonAsync(
            "/ai/talk/ticket",
            JsonSerializer.Serialize(new { conversation }),
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        var ticket = doc.RootElement.GetProperty("ticket").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ticket));
        Assert.Equal(30, doc.RootElement.GetProperty("expiresInSeconds").GetInt32());
        return ticket!;
    }

    private static Uri TalkUri(VoiceHostHarness host, string conversation, string? ticket = null)
    {
        var query = ticket is null
            ? $"conversation={Uri.EscapeDataString(conversation)}"
            : $"conversation={Uri.EscapeDataString(conversation)}&ticket={Uri.EscapeDataString(ticket)}";
        return new UriBuilder(host.BaseAddress)
        {
            Scheme = host.BaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ai/talk",
            Query = query,
        }.Uri;
    }

    private sealed class NoopRealtimeClient : IRealtimeClient
    {
        public Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IRealtimeClientSession>(new NoopSession(options));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private sealed class NoopSession(RealtimeSessionOptions? options) : IRealtimeClientSession
        {
            private readonly Channel<RealtimeServerMessage> _channel = Channel.CreateUnbounded<RealtimeServerMessage>();
            public RealtimeSessionOptions? Options => options;
            public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken)) { yield return item; }
            }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public ValueTask DisposeAsync() { _channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
        }
    }
}
