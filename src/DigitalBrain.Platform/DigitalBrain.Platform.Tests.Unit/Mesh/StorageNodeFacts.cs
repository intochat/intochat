using System.Security.Cryptography;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

// T23-1…T23-4, T23-7, R114: a volunteer node answers put, get, prove and drop over a machine link, for registered
// machines, under quotas, keeping only ciphertext named by its content id.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class StorageNodeFacts
{
    internal static StorageNodeOptions Options(long offered, long perAccount = 0, string? directory = null) => new()
    {
        OfferedBytes = offered, MaxBytesPerAccount = perAccount, Directory = directory ?? Path.Combine(Path.GetTempPath(), $"node-{Guid.NewGuid():N}"),
    };

    // A node machine of account bob listening on loopback, its end dispatching to a StorageNodeEndpoint over `store`.
    private sealed class Node : IAsyncDisposable
    {
        private readonly TrustHost _trust;
        private readonly FileMachineSigningKey _key;
        private readonly QuicLinkListener _listener;
        private readonly string _deployment;
        private readonly List<IAsyncDisposable> _links = [];

        private Node(TrustHost trust, FileMachineSigningKey key, QuicLinkListener listener, string deployment) =>
            (_trust, _key, _listener, _deployment) = (trust, key, listener, deployment);

        public static async Task<Node> Start(TrustHost trust, ShardStore store, CancellationToken ct)
        {
            var key = Key();
            await Register(trust, key, "bob");
            var deployment = await Deployment(trust);
            var endpoint = new StorageNodeEndpoint(store, GrainTrustClients.Reading(() => trust.Grains, TimeProvider.System), Serializer, TimeProvider.System);
            var certificate = LinkCertificates.Create(key, deployment, TimeProvider.System);
            var end = new LinkEnd(() => certificate, deployment, _ => true, Links.LinkTestbed.Options, Serializer,
                endpoint.Answer, TimeProvider.System);
            return new Node(trust, key, await QuicLinkListener.Start(Loopback, end, _ => { }, ct), deployment);
        }

        // A registered machine of `account` linked to this node.
        public async Task<Putter> Dial(string account, CancellationToken ct)
        {
            var key = Key();
            await Register(_trust, key, account);
            var link = await QuicMachineLink.Dial(_listener.LocalEndPoint, null, MachineKeys.IdOf(_key.PublicKey),
                End(LinkCertificates.Create(key, _deployment, TimeProvider.System), _deployment, _ => true), LinkPath.Direct, ct);
            _links.Add(link);
            return new Putter(link);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var link in _links) { await link.DisposeAsync(); }
            await _listener.DisposeAsync();
            _key.Dispose();
        }
    }

    private sealed class Putter(QuicMachineLink link)
    {
        public async Task<TResult> Call<TArgs, TResult>(string operation, TArgs args, CancellationToken ct)
            => Serializer.Deserialize<TResult>(LinkReplies.Payload(await link.Send(new LinkRequest(operation, Serializer.SerializeToArray(args), null, null, Guid.NewGuid()), ct)))!;

        public Task Put(byte[] bytes, CancellationToken ct, string? cid = null) => Call<ShardPut, bool>(NodeOperations.Put, new ShardPut(cid ?? StorageProof.ContentId(bytes), bytes), ct);

        public Task<byte[]?> Get(byte[] bytes, CancellationToken ct) => Call<ShardGet, byte[]?>(NodeOperations.Get, new ShardGet(StorageProof.ContentId(bytes)), ct);

        public Task<bool> Drop(byte[] bytes, CancellationToken ct) => Call<ShardDrop, bool>(NodeOperations.Drop, new ShardDrop(StorageProof.ContentId(bytes)), ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeStoresAShardOnlyUnderItsContentId()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var node = await Node.Start(trust, new ShardStore(Options(1 << 20), 1 << 16), ct);
        var alice = await node.Dial("alice", ct);
        var shard = RandomNumberGenerator.GetBytes(64);

        await Assert.ThrowsAsync<InvalidDataException>(() => alice.Put(shard, ct, StorageProof.ContentId([1, 2, 3])));
        await Assert.ThrowsAsync<InvalidDataException>(() => alice.Put(shard, ct, "../../escape"));
        await alice.Put(shard, ct);

        Assert.Equal(shard, await alice.Get(shard, ct));
        Assert.Equal(StorageProof.Compute([9], shard), await alice.Call<ShardProve, byte[]?>(NodeOperations.Prove, new ShardProve(StorageProof.ContentId(shard), [9]), ct));
        Assert.Null(await alice.Get([4, 5, 6], ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARepeatedPutOfAHeldShardIsNeverRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var shard = RandomNumberGenerator.GetBytes(64);
        var store = new ShardStore(Options(64, 64), 1 << 16);
        await using var trust = await TrustHost.StartAsync(ct);
        await using var node = await Node.Start(trust, store, ct);
        var alice = await node.Dial("alice", ct);
        var carol = await node.Dial("carol", ct);

        await alice.Put(shard, ct);
        await alice.Put(shard, ct);
        await carol.Put(shard, ct);

        Assert.Equal(64, store.UsedBytes);
        Assert.Equal(shard, await carol.Get(shard, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeRefusesAShardBeyondItsOffer()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var node = await Node.Start(trust, new ShardStore(Options(100, 100), 1 << 16), ct);
        var alice = await node.Dial("alice", ct);
        await alice.Put(RandomNumberGenerator.GetBytes(64), ct);
        var second = RandomNumberGenerator.GetBytes(64);

        await Assert.ThrowsAsync<NodeFullException>(() => alice.Put(second, ct));
        Assert.Null(await alice.Get(second, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeRefusesAnAccountBeyondItsShare()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var node = await Node.Start(trust, new ShardStore(Options(1000), 1 << 16), ct);
        var alice = await node.Dial("alice", ct);
        var carol = await node.Dial("carol", ct);
        await alice.Put(RandomNumberGenerator.GetBytes(200), ct);

        await Assert.ThrowsAsync<NodeFullException>(() => alice.Put(RandomNumberGenerator.GetBytes(100), ct));
        await carol.Put(RandomNumberGenerator.GetBytes(200), ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ANodeRefusesAShardLargerThanTheCap()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var node = await Node.Start(trust, new ShardStore(Options(1 << 20), 64), ct);
        var alice = await node.Dial("alice", ct);
        var shard = RandomNumberGenerator.GetBytes(65);

        await Assert.ThrowsAsync<InvalidDataException>(() => alice.Put(shard, ct));
        Assert.Null(await alice.Get(shard, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ADropByAnAccountWithoutAClaimChangesNothing()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var store = new ShardStore(Options(1 << 20), 1 << 16);
        await using var trust = await TrustHost.StartAsync(ct);
        await using var node = await Node.Start(trust, store, ct);
        var alice = await node.Dial("alice", ct);
        var bob = await node.Dial("mallory", ct);
        var shard = RandomNumberGenerator.GetBytes(64);
        await alice.Put(shard, ct);

        Assert.True(await bob.Drop(shard, ct));
        Assert.Equal(shard, await alice.Get(shard, ct));
        Assert.True(await bob.Drop(RandomNumberGenerator.GetBytes(64), ct));

        Assert.True(await alice.Drop(shard, ct));
        Assert.Null(await alice.Get(shard, ct));
        Assert.Equal(0, store.UsedBytes);
    }

    [Fact]
    public async Task ANodeKeepsItsShardsAcrossARestart()
    {
        var options = Options(1 << 20);
        var shard = RandomNumberGenerator.GetBytes(64);
        var cid = StorageProof.ContentId(shard);
        await new ShardStore(options, 1 << 16).Put(cid, shard, "machine-a", ["alice"]);

        var restarted = new ShardStore(options, 1 << 16);

        Assert.Equal(shard, await restarted.Get(cid));
        Assert.Equal(64, restarted.UsedBytes);
        await restarted.Drop(cid, "machine-b", ["alice"]);
        Assert.Null(await new ShardStore(options, 1 << 16).Get(cid));
    }

    // R99: at most 16 claims; a 17th putter is a placement miss unless the oldest claimant is no longer registered.
    [Fact]
    public async Task AShardTakesAtMostSixteenClaims()
    {
        var store = new ShardStore(Options(1 << 20), 1 << 16);
        var shard = RandomNumberGenerator.GetBytes(64);
        var cid = StorageProof.ContentId(shard);
        for (var n = 0; n < ShardStore.MaxClaims; n++) { await store.Put(cid, shard, $"machine-{n}", [$"account-{n}"], _ => Task.FromResult(true)); }

        await Assert.ThrowsAsync<NodeFullException>(() => store.Put(cid, shard, "machine-late", ["late"], _ => Task.FromResult(true)));
        await store.Put(cid, shard, "machine-late", ["late"], machine => Task.FromResult(machine != "machine-0"));

        await store.Drop(cid, "machine-late", ["late"]);
        Assert.Equal(shard, await store.Get(cid));
        await store.Drop(cid, "machine-0", ["account-0"]);
        Assert.Equal(shard, await store.Get(cid));
    }

    [Fact]
    public void ANodeSeesOnlyCiphertextAndContentIds()
    {
        string[] allowed = [nameof(ShardPut.Cid), nameof(ShardPut.Bytes), nameof(ShardProve.Nonce)];
        foreach (var request in new[] { typeof(ShardPut), typeof(ShardGet), typeof(ShardProve), typeof(ShardDrop) })
        {
            Assert.All(request.GetProperties().Where(property => property.Name != "EqualityContract"), property => Assert.Contains(property.Name, allowed));
        }
    }
}
