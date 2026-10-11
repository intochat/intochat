using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;
using RawMachine = DigitalBrain.Platform.Tests.Unit.Links.TrustLinkFacts.RawMachine;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

// Volunteer machines registered straight into a host's own trust grains (single host), with their nodes signed in.
internal static class Volunteers
{
    private static long _counter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static MachineSigned<T> Sign<T>(FileMachineSigningKey key, T payload, string account)
    {
        var machineId = MachineKeys.IdOf(key.PublicKey);
        var now = DateTimeOffset.UtcNow;
        var counter = Interlocked.Increment(ref _counter);
        return new MachineSigned<T>(payload, account, machineId, now, counter, key.Sign(MachineSigned<T>.SignedBytes(payload, account, machineId, now, counter)));
    }

    // A volunteer machine of `account` on a host whose trust grains are local (single host): registered and its node signed in.
    internal static async Task<string> Register(IGrainFactory grains, string account, long offered)
    {
        var key = FileMachineSigningKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"volunteer-{Guid.NewGuid():N}", "identity.key"));
        var id = MachineKeys.IdOf(key.PublicKey);
        var pairing = MachineRegistrar.HashOf(account + "-pairing");
        var agreement = Sequencer.SequencerFacts.Agreement();
        await grains.GetGrain<IMachineRegistry>(id).Register(key.PublicKey, agreement, account, pairing,
            key.Sign(Signing.Registration(id, key.PublicKey, agreement, account, pairing)));
        await grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key)
            .RegisterNode(Sign(key, new NodeRegistration(StorageNodes.IdOf(id), id, account, offered), account));
        return StorageNodes.IdOf(id);
    }
}

// T23-5/T23-6/T23-10, R112: volunteer nodes in the directory — signed registration, heartbeats, placement by room and
// owner, and audit reports bound to placements.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class NodeDirectoryFacts
{
    private static Task RegisterNode(RawMachine machine, string account, long offered, CancellationToken ct, string? nodeId = null)
        => machine.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            machine.Sign(new NodeRegistration(nodeId ?? StorageNodes.IdOf(machine.MachineId), machine.MachineId, account, offered), account), ct);

    private static async Task<string[]> Place(RawMachine machine, int count, int shardBytes, CancellationToken ct)
        => (await machine.Call<(int, string[], int), Placement>(TrustOperations.NodePlace, (count, Array.Empty<string>(), shardBytes), ct)).Nodes;

    private static Task<AuditReport> Report(RawMachine machine, AuditReport report, CancellationToken ct)
        => machine.Call<AuditReport, AuditReport>(TrustOperations.NodeAudit, report, ct);

    private static async Task<RawMachine> Machine(TrustHost trust, string account, CancellationToken ct)
    {
        var machine = await RawMachine.Dial(trust, ct);
        await machine.Register(account, ct);
        return machine;
    }

    private static async Task<StorageNodeView> View(TrustHost trust, string nodeId)
        => (await trust.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List()).Single(node => node.NodeId == nodeId);

    [Fact(Timeout = 120_000)]
    public async Task ANodeRegistersSignedByItsMachineWithItsAccountAsOwner()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var bob = await Machine(trust, "bob", ct);

        await RegisterNode(bob, "bob", 1000, ct);

        var node = await View(trust, StorageNodes.IdOf(bob.MachineId));
        Assert.Equal(("bob", bob.MachineId, 1000L, true), (node.OwnerAccount, node.MachineId, node.OfferedBytes, node.Online));
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeRegistrationSignedByAnotherMachineIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var bob = await Machine(trust, "bob", ct);
        await using var carol = await Machine(trust, "carol", ct);
        var carols = carol.Sign(new NodeRegistration(StorageNodes.IdOf(carol.MachineId), carol.MachineId, "carol", 1000), "carol");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned, carols, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RegisterNode(bob, "bob", 1000, ct, StorageNodes.IdOf(carol.MachineId)));
        Assert.DoesNotContain(await trust.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List(),
            node => node.NodeId == StorageNodes.IdOf(carol.MachineId));
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeRegistrationForAnAccountTheMachineDoesNotServeIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var bob = await Machine(trust, "bob", ct);
        await using var carol = await Machine(trust, "carol", ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RegisterNode(bob, "carol", 1000, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeCannotBeRegisteredForTheAccountPlatform()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var bob = await Machine(trust, "bob", ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RegisterNode(bob, SeedNodeRegistration.Owner, 1000, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RegisterNode(bob, "bob", 1000, ct, "seed-0"));
        Assert.Null((await View(trust, "seed-0")).MachineId);
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeThatMissesHeartbeatsIsOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await TestBrain.Create()
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(MeshOptions.Key, time)).StartAsync(ct);
        var directory = brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);
        var node = await Volunteers.Register(brain.Grains, "bob", 1000);
        var offlineAfter = new MeshOptions().OfflineAfter;

        time.Advance(offlineAfter - TimeSpan.FromSeconds(1));
        Assert.True((await directory.List()).Single(n => n.NodeId == node).Online);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await directory.List()).Single(n => n.NodeId == node).Online);
        await directory.Register("seed-0", SeedNodeRegistration.Owner);
        Assert.Equal(["seed-0"], await directory.Place(1, [], 16, ["alice"]));

        await directory.Heartbeat(node["node/".Length..], node, 10);
        var back = (await directory.List()).Single(n => n.NodeId == node);
        Assert.Equal((true, 10L), (back.Online, back.UsedBytes));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.Heartbeat("another-machine", node, 0));
    }

    [Fact(Timeout = 120_000)]
    public async Task PlacementSkipsANodeWithoutRoomForTheShard()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var alice = await Machine(trust, "alice", ct);
        await using var bob = await Machine(trust, "bob", ct);
        await RegisterNode(bob, "bob", 100, ct);
        var bobs = StorageNodes.IdOf(bob.MachineId);

        Assert.DoesNotContain(bobs, await Place(alice, 8, 101, ct));
        Assert.Contains(bobs, await Place(alice, 8, 100, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task PlacementNeverPicksANodeOfThePuttersAccount()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var alice = await Machine(trust, "alice", ct);
        await using var bob = await Machine(trust, "bob", ct);
        await RegisterNode(bob, "bob", 1000, ct);
        var bobs = StorageNodes.IdOf(bob.MachineId);

        Assert.DoesNotContain(bobs, await Place(bob, 8, 16, ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Place(bob, 9, 16, ct));
        Assert.Contains(bobs, await Place(alice, 1, 16, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task PlacementFallsBackToSeedsWhenNoVolunteerIsOnline()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await TestBrain.Create()
            .ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(MeshOptions.Key, time)).StartAsync(ct);
        var directory = brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);
        for (var n = 0; n < 8; n++) { await directory.Register($"seed-{n}", SeedNodeRegistration.Owner); }
        await Volunteers.Register(brain.Grains, "bob", 1000);
        await Volunteers.Register(brain.Grains, "carol", 1000);
        time.Advance(new MeshOptions().OfflineAfter + TimeSpan.FromSeconds(1));

        var placed = await directory.Place(8, [], 16, ["alice"]);

        var nodes = (await directory.List()).ToDictionary(node => node.NodeId);
        Assert.All(placed, id => Assert.Equal(SeedNodeRegistration.Owner, nodes[id].OwnerAccount));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAuditIsReportedByTheAuditorNotTheOwner()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var alice = await Machine(trust, "alice", ct);
        await using var bob = await Machine(trust, "bob", ct);
        await RegisterNode(bob, "bob", 1000, ct);
        var bobs = StorageNodes.IdOf(bob.MachineId);
        Assert.Contains(bobs, await Place(alice, 1, 16, ct));

        await Report(alice, new AuditReport(bobs, "sha256:00", 0, false, 16), ct);

        Assert.Equal(0.5, (await View(trust, bobs)).Reputation);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnOwnerCannotReportOnItsOwnNode()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var bob = await Machine(trust, "bob", ct);
        await RegisterNode(bob, "bob", 1000, ct);
        var bobs = StorageNodes.IdOf(bob.MachineId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Report(bob, new AuditReport(bobs, "sha256:00", 0, true, 16), ct));
        Assert.Equal(1.0, (await View(trust, bobs)).Reputation);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAccountCannotReportOnANodeItWasNotPlacedOn()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var alice = await Machine(trust, "alice", ct);
        await using var bob = await Machine(trust, "bob", ct);
        await using var carol = await Machine(trust, "carol", ct);
        await RegisterNode(bob, "bob", 1000, ct);
        var bobs = StorageNodes.IdOf(bob.MachineId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Report(carol, new AuditReport(bobs, "sha256:00", 0, false, 16), ct));
        Assert.Equal(1.0, (await View(trust, bobs)).Reputation);

        Assert.Contains(bobs, await Place(alice, 1, 16, ct));
        var recorded = await Report(alice, new AuditReport(bobs, "sha256:00", 0, true, int.MaxValue), ct);
        Assert.Equal(new MeshOptions().MaxShardBytes, recorded.ShardBytes);
    }
}
