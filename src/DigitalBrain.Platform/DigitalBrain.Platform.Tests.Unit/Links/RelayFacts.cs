using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Links;

// T20-5/T20-6: when a peer cannot be reached directly the link goes through the trust host's relay, which forwards
// QUIC datagrams between exactly the two machines of a slot and can neither read nor alter what they say.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class RelayFacts
{
    [Fact(Timeout = 120_000)]
    public async Task ALinkFallsBackToTheRelayWhenThePeerCannotBeReachedDirectly()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var pair = await MachinePair.Start(ct);

        var link = await pair.A.Connect(pair.B.LocalMachineId, ct);
        var payload = Marker();
        var reply = await link.Send(Request(payload), ct);

        Assert.Equal(LinkPath.Relayed, link.Path);
        Assert.Equal(payload, reply.Payload);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARelayThatFlipsAByteIsDetected()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var pair = await MachinePair.Start(ct, idleTimeout: TimeSpan.FromSeconds(2));
        var link = await pair.A.Connect(pair.B.LocalMachineId, ct);
        Assert.Equal(LinkPath.Relayed, link.Path);

        pair.Relay.Tamper = true;
        var clock = Stopwatch.StartNew();
        LinkReply? reply = null;
        await Assert.ThrowsAsync<LinkFailedException>(async () => reply = await link.Send(Request(Marker()), ct));

        Assert.Null(reply);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The tampered link failed after {clock.Elapsed}.");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheRelaySeesOnlyCiphertext()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var pair = await MachinePair.Start(ct);
        var forwarded = new List<byte[]>();
        pair.Relay.Forwarded = datagram => { lock (forwarded) { forwarded.Add(datagram); } };

        var link = await pair.A.Connect(pair.B.LocalMachineId, ct);
        await link.Send(Request(Marker()), ct);

        lock (forwarded)
        {
            Assert.NotEmpty(forwarded);
            Assert.DoesNotContain(forwarded, datagram => datagram.AsSpan().IndexOf(MarkerText) >= 0);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ARelaySlotForwardsOnlyBetweenItsTwoMachines()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var pair = await MachinePair.Start(ct);
        var link = await pair.A.Connect(pair.B.LocalMachineId, ct);
        Assert.Equal(LinkPath.Relayed, link.Path);
        var strangers = Marker(64);
        var leaked = 0;
        pair.Relay.Forwarded = datagram => { if (datagram.AsSpan().IndexOf(strangers) >= 0) { Interlocked.Increment(ref leaked); } };

        using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] join = [.. Relay.SlotAsk, .. RandomNumberGenerator.GetBytes(Relay.TokenSize)];
        await stranger.SendToAsync(join, SocketFlags.None, pair.Trust.Relay, ct);
        await stranger.SendToAsync(strangers, SocketFlags.None, pair.Trust.Relay, ct);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromMilliseconds(500));
        var answered = false;
        try
        {
            await stranger.ReceiveFromAsync(new byte[2048], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token);
            answered = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        Assert.False(answered);
        Assert.Equal(0, Volatile.Read(ref leaked));
    }

    // A trust host and two machines of one owner over it; B cannot be reached directly.
    private sealed class MachinePair : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _owned = [];

        public required TrustHost Trust { get; init; }

        public required QuicMachineTransport A { get; init; }

        public required QuicMachineTransport B { get; init; }

        public Relay Relay => Trust.Services.GetRequiredService<TrustLinkEndpoint>().Relay;

        public static async Task<MachinePair> Start(CancellationToken cancellationToken, TimeSpan? idleTimeout = null)
        {
            var trust = await TrustHost.StartAsync(cancellationToken);
            var idle = idleTimeout ?? TimeSpan.FromSeconds(15);
            TestBrain Machine() => ModuleTest.Create().WithTrust(trust)
                .WithConfiguration($"{LinkOptions.Key}:{nameof(LinkOptions.DirectWithin)}", "00:00:01")
                .WithConfiguration($"{LinkOptions.Key}:{nameof(LinkOptions.PunchFor)}", "00:00:00.300")
                .WithConfiguration($"{LinkOptions.Key}:{nameof(LinkOptions.IdleTimeout)}", idle.ToString("c"))
                .WithConfiguration($"{LinkOptions.Key}:{nameof(LinkOptions.KeepAlive)}", (idle / 3).ToString("c"));
            var a = await Machine().StartAsync(cancellationToken);
            var b = await Machine().StartAsync(cancellationToken);
            var pair = new MachinePair
            {
                Trust = trust,
                A = a.SiloServices.GetRequiredService<QuicMachineTransport>(),
                B = b.SiloServices.GetRequiredService<QuicMachineTransport>(),
            };
            pair._owned.AddRange([b, a, trust]);
            pair.B.DirectBlocked = true;
            pair.B.Handle("echo", Echo);
            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var owned in _owned) { await owned.DisposeAsync(); }
        }
    }
}
