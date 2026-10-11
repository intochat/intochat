using System.Diagnostics;
using System.Text.Json;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Links;

// T20-6: a rendezvous through the trust host, a punch from raw sockets and QUIC re-bound on the punched ports.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class HolePunchFacts
{
    public const string FieldPeer = "DIGITALBRAIN_FIELD_PEER";
    public const string FieldRole = "DIGITALBRAIN_FIELD_ROLE";
    public const string FieldPeerMachine = "DIGITALBRAIN_FIELD_PEER_MACHINE";

    [Fact(Timeout = 120_000)]
    public async Task TwoMachinesLinkDirectlyAfterARendezvousOnLoopback()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        await using var b = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        var (ta, tb) = (a.SiloServices.GetRequiredService<QuicMachineTransport>(), b.SiloServices.GetRequiredService<QuicMachineTransport>());
        tb.Handle("echo", Echo);
        ta.Handle("echo", Echo);

        var link = await ta.Connect(tb.LocalMachineId, ct);
        var payload = Marker();

        Assert.Equal(LinkPath.Direct, link.Path);
        Assert.Equal(payload, (await link.Send(Request(payload), ct)).Payload);
        // The link is one per pair: the peer reuses it the other way.
        var back = await tb.Connect(ta.LocalMachineId, ct);
        Assert.Equal(payload, (await back.Send(Request(payload), ct)).Payload);
    }

    // Live-gated (Task 21 Step 9): two machines on two real networks, each running this fact against one trust
    // endpoint reachable from both (DIGITALBRAIN_FIELD_PEER = host:port of that trust endpoint, DIGITALBRAIN_FIELD_ROLE
    // = a on one network and b on the other). Each side makes 50 Connect attempts to the other, records how many
    // came up direct and how many relayed, the handshake and 1 KB medians, in {MachineDirectory}/field-test.json.
    // It asserts only that every attempt connected; the controller weighs the direct share against Task 20's
    // literature figures.
    [Fact(Timeout = 600_000)]
    public async Task AHolePunchAcrossTwoNatsIsMeasured()
    {
        if (Environment.GetEnvironmentVariable(FieldPeer) is not { Length: > 0 } endpoint
            || Environment.GetEnvironmentVariable(FieldRole) is not { } role || role is not ("a" or "b"))
        { Assert.Skip("needs two networks"); return; }
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var machine = await TestBrain.Create()
            .WithConfiguration($"{DigitalBrain.Platform.Trust.TrustOptions.Key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.Endpoint)}", endpoint)
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.MachineDirectory)}", Path.Combine(Path.GetTempPath(), "digitalbrain-field", role))
            .StartAsync(ct);
        var transport = machine.SiloServices.GetRequiredService<QuicMachineTransport>();
        transport.Handle("echo", Echo);
        var directory = machine.SiloServices.GetRequiredService<IOptions<PartsOptions>>().Value.MachineDirectory;
        // Each side writes its machine id to {MachineDirectory}/machine-id on a first run; the operator hands it to the
        // other side as DIGITALBRAIN_FIELD_PEER_MACHINE and runs both again.
        var peer = Environment.GetEnvironmentVariable(FieldPeerMachine);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "machine-id"), transport.LocalMachineId, ct);
        if (string.IsNullOrWhiteSpace(peer)) { Assert.Skip($"needs DIGITALBRAIN_FIELD_PEER_MACHINE (this side is {transport.LocalMachineId})"); return; }

        var (direct, relayed) = (0, 0);
        var handshakes = new List<double>();
        var calls = new List<double>();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var clock = Stopwatch.StartNew();
            await using var link = await transport.Connect(peer, ct);
            handshakes.Add(clock.Elapsed.TotalMilliseconds);
            if (link.Path == LinkPath.Direct) { direct++; } else { relayed++; }
            clock.Restart();
            await link.Send(Request(Marker()), ct);
            calls.Add(clock.Elapsed.TotalMilliseconds);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "field-test.json"), JsonSerializer.Serialize(new
        {
            role, machine = transport.LocalMachineId, peer, direct, relayed,
            handshakeMedianMs = Median(handshakes), oneKilobyteMedianMs = Median(calls), at = DateTimeOffset.UtcNow,
        }), ct);
        Assert.Equal(50, direct + relayed);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count == 0 ? 0 : values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }
}
