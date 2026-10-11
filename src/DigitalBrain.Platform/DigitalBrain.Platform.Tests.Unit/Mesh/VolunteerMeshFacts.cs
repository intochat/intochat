using System.Security.Cryptography;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

// A node that serves other bytes than it was given, on every get (and so on every proof).
internal sealed class LyingShardStore(StorageNodeOptions options, int maxShardBytes) : ShardStore(options, maxShardBytes)
{
    public override async Task<byte[]?> Get(string cid) => await base.Get(cid) is { } held ? RandomNumberGenerator.GetBytes(held.Length) : null;
}

// A node that kept only each shard's digest and answers proofs from it.
internal sealed class DigestShardStore(StorageNodeOptions options, int maxShardBytes) : ShardStore(options, maxShardBytes)
{
    public override async Task<byte[]?> Prove(string cid, byte[] nonce)
        => await base.Get(cid) is { } held ? StorageProof.Compute(nonce, SHA256.HashData(held)) : null;
}

// Facts that run four or five machine silos at once run alone, so they do not starve the link facts' handshakes.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VolunteerMachines
{
    public const string Name = "Volunteer machines";
}

// Task 24 (T23-1…T23-9, Q25): a putter's segments spread over volunteer nodes of other accounts and the seeds; a lying,
// digest-only, full or vanished node costs at most an erasure.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
[Collection(VolunteerMachines.Name)]
public sealed class VolunteerMeshFacts
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    internal static TestBrain Coded(TestBrain brain)
        => brain.WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ManifestReplicas)}", "2");

    // A machine of `account` that offers storage; `store` replaces its shard store (a lying node, say).
    internal static async Task<ITestBrain> NodeMachine(TrustHost trust, string account, CancellationToken ct,
        Func<StorageNodeOptions, int, ShardStore>? store = null, long offered = 1 << 20, long perAccount = 0)
    {
        var builder = TestBrain.Create().WithTrust(trust)
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Secured))
            .WithConfiguration($"{StorageNodeOptions.Key}:{nameof(StorageNodeOptions.OfferedBytes)}", offered.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithConfiguration($"{StorageNodeOptions.Key}:{nameof(StorageNodeOptions.MaxBytesPerAccount)}", perAccount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (store is not null)
        {
            builder = builder.ConfigureSilo(silo => silo.Services.AddSingleton(provider => store(
                provider.GetRequiredService<IOptions<StorageNodeOptions>>().Value, provider.GetRequiredService<IOptions<MeshOptions>>().Value.MaxShardBytes)));
        }
        var machine = await builder.StartAsync(ct);
        // A Secured host without a bootstrap credential binds nothing at start; this one serves `account` only.
        await machine.SiloServices.GetRequiredService<MachineRegistrar>().Bind(account);
        await machine.SiloServices.GetRequiredService<StorageNodeService>().Register();
        return machine;
    }

    internal static string NodeOf(ITestBrain machine) => machine.SiloServices.GetRequiredService<StorageNodeService>().NodeId;

    internal static async Task<SegmentManifest> Manifest(ITestBrain putter, byte[] key, MeshRef reference, CancellationToken ct)
    {
        var shards = putter.SiloServices.GetRequiredService<IShardClient>();
        foreach (var node in reference.ManifestNodes)
        {
            if (await shards.Get(node, reference.ManifestCid, ct) is not { } bytes || StorageProof.ContentId(bytes) != reference.ManifestCid) { continue; }
            var plain = SegmentSeal.Open(key, $"mesh-manifest|{reference.SegmentId}", bytes);
            return putter.SiloServices.GetRequiredService<Serializer>().Deserialize<SegmentManifest>(plain)!;
        }
        throw new InvalidOperationException("No manifest replica is readable.");
    }

    private static async Task Stop(params IAsyncDisposable?[] hosts)
    {
        foreach (var host in hosts) { if (host is not null) { await host.DisposeAsync(); } }
    }

    [Fact(Timeout = 240_000)]
    public async Task ASegmentIsWrittenAcrossVolunteersAndSeedsAndReadBack()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var trust = await TrustHost.StartAsync(ct);
        ITestBrain? p = null, w = null, x = null, y = null, z = null;
        try
        {
            p = await Coded(TestBrain.Create().WithTrust(trust)).StartAsync(ct);
            // A node of the putter's own account: placement never picks it (T23-6).
            w = await Coded(TestBrain.Create().WithTrust(trust))
                .WithConfiguration($"{StorageNodeOptions.Key}:{nameof(StorageNodeOptions.OfferedBytes)}", "1048576").StartAsync(ct);
            await w.SiloServices.GetRequiredService<StorageNodeService>().Register();
            (x, y, z) = (await NodeMachine(trust, "bob", ct), await NodeMachine(trust, "carol", ct), await NodeMachine(trust, "dave", ct));
            var data = RandomNumberGenerator.GetBytes(3000);
            var store = p.SiloServices.GetRequiredService<IMeshStore>();

            var reference = await store.Write(Key, "part/log/1", data, ct);

            var nodes = (await Manifest(p, Key, reference, ct)).Shards.Select(shard => shard.NodeId).ToArray();
            Assert.All([x, y, z], node => Assert.Single(nodes, NodeOf(node)));
            Assert.DoesNotContain(NodeOf(w), nodes);
            Assert.DoesNotContain(NodeOf(w), reference.ManifestNodes);
            Assert.Equal(data, await store.Read(Key, reference, ct));
            Assert.True(await x.SiloServices.GetRequiredService<ShardStore>().Get(
                (await Manifest(p, Key, reference, ct)).Shards.Single(shard => shard.NodeId == NodeOf(x)).Cid) is not null);
        }
        finally { await Stop(z, y, x, w, p, trust); }
    }

    [Fact(Timeout = 240_000)]
    public async Task ALyingNodeServesOtherBytesAndTheReadSurvives()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var trust = await TrustHost.StartAsync(ct);
        ITestBrain? p = null, x = null, y = null, z = null;
        try
        {
            p = await Coded(TestBrain.Create().WithTrust(trust)).StartAsync(ct);
            x = await NodeMachine(trust, "bob", ct, (options, cap) => new LyingShardStore(options, cap));
            (y, z) = (await NodeMachine(trust, "carol", ct), await NodeMachine(trust, "dave", ct));
            var data = RandomNumberGenerator.GetBytes(3000);
            var store = p.SiloServices.GetRequiredService<IMeshStore>();
            var reference = await store.Write(Key, "part/log/1", data, ct);
            var manifest = await Manifest(p, Key, reference, ct);
            Assert.Contains(manifest.Shards, shard => shard.NodeId == NodeOf(x));

            Assert.Equal(data, await store.Read(Key, reference, ct));

            var audit = await store.Audit(Key, reference, 0, ct);
            Assert.All(audit.Where(result => result.NodeId == NodeOf(x)), result => Assert.Equal((false, "wrong proof"), (result.Passed, result.Reason)));
            Assert.All(audit.Where(result => result.NodeId != NodeOf(x)), result => Assert.True(result.Passed, result.Reason));
        }
        finally { await Stop(z, y, x, p, trust); }
    }

    [Fact(Timeout = 240_000)]
    public async Task ANodeThatAnswersProofsWithoutDataFailsAudit()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var trust = await TrustHost.StartAsync(ct);
        ITestBrain? p = null, x = null, y = null, z = null;
        try
        {
            p = await Coded(TestBrain.Create().WithTrust(trust)).StartAsync(ct);
            y = await NodeMachine(trust, "carol", ct, (options, cap) => new DigestShardStore(options, cap));
            (x, z) = (await NodeMachine(trust, "bob", ct), await NodeMachine(trust, "dave", ct));
            var store = p.SiloServices.GetRequiredService<IMeshStore>();
            var reference = await store.Write(Key, "part/log/1", RandomNumberGenerator.GetBytes(3000), ct);

            var audit = await store.Audit(Key, reference, 0, ct);

            var ys = Assert.Single(audit, result => result.NodeId == NodeOf(y));
            Assert.Equal((false, "wrong proof"), (ys.Passed, ys.Reason));
            var view = (await trust.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List()).Single(node => node.NodeId == NodeOf(y));
            Assert.True(view.Reputation < 1.0);
        }
        finally { await Stop(z, y, x, p, trust); }
    }

    [Fact(Timeout = 240_000)]
    public async Task AnOfflineNodesShardsAreRepairedOntoOtherOwners()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var trust = await TrustHost.StartAsync(ct, configure: silo => silo.Services.AddKeyedSingleton<TimeProvider>(MeshOptions.Key, time));
        ITestBrain? p = null, x = null, y = null, z = null, v = null;
        try
        {
            p = await Coded(TestBrain.Create().WithTrust(trust)).StartAsync(ct);
            (x, y, z) = (await NodeMachine(trust, "bob", ct), await NodeMachine(trust, "carol", ct), await NodeMachine(trust, "dave", ct));
            var data = RandomNumberGenerator.GetBytes(3000);
            var store = p.SiloServices.GetRequiredService<IMeshStore>();
            var reference = await store.Write(Key, "part/log/1", data, ct);
            var lost = (await Manifest(p, Key, reference, ct)).Shards.Single(shard => shard.NodeId == NodeOf(z));
            // A new owner joins after the write: the only owner holding no shard of the segment.
            v = await NodeMachine(trust, "erin", ct);

            // Z stops (no more heartbeats); the others beat after OfflineAfter has passed.
            var zs = NodeOf(z);
            await z.DisposeAsync();
            z = null;
            time.Advance(new MeshOptions().OfflineAfter + TimeSpan.FromSeconds(1));
            foreach (var beating in (ITestBrain[])[x, y, v]) { await beating.SiloServices.GetRequiredService<StorageNodeService>().Heartbeat(); }
            Assert.False((await trust.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List()).Single(node => node.NodeId == zs).Online);

            var repair = await store.Repair(Key, reference, cancellationToken: ct);

            var move = Assert.Single(repair.Moved, moved => moved.FromNode == zs);
            Assert.Equal((lost.Index, NodeOf(v)), (move.Index, move.ToNode));
            Assert.Equal(data, await store.Read(Key, repair.Reference, ct));
        }
        finally { await Stop(v, z, y, x, p, trust); }
    }

    [Fact(Timeout = 240_000)]
    public async Task AFullNodeIsReplacedAtPlacement()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var trust = await TrustHost.StartAsync(ct);
        ITestBrain? p = null, x = null, y = null, z = null;
        try
        {
            p = await Coded(TestBrain.Create().WithTrust(trust)).StartAsync(ct);
            // X offers room on record, but takes less than one shard from any one account: every put to it is refused.
            x = await NodeMachine(trust, "bob", ct, perAccount: 16);
            (y, z) = (await NodeMachine(trust, "carol", ct), await NodeMachine(trust, "dave", ct));
            var data = RandomNumberGenerator.GetBytes(3000);
            var store = p.SiloServices.GetRequiredService<IMeshStore>();

            var reference = await store.Write(Key, "part/log/1", data, ct);

            var manifest = await Manifest(p, Key, reference, ct);
            Assert.DoesNotContain(manifest.Shards, shard => shard.NodeId == NodeOf(x));
            Assert.DoesNotContain(NodeOf(x), reference.ManifestNodes);
            Assert.Equal(0, x.SiloServices.GetRequiredService<ShardStore>().UsedBytes);
            Assert.Equal(data, await store.Read(Key, reference, ct));
        }
        finally { await Stop(z, y, x, p, trust); }
    }

    [Fact(Timeout = 60_000)]
    public async Task ASegmentLargerThanTheShardCapIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithPartLog()
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.MaxShardBytes)}", "1024").StartAsync(ct);
        var store = brain.SiloServices.GetRequiredService<IMeshStore>();

        await store.Write(Key, "part/log/1", RandomNumberGenerator.GetBytes(4 * 1024 - 64), ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Write(Key, "part/log/2", RandomNumberGenerator.GetBytes(5 * 1024), ct));
    }
}
