using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

// T23-9, R7, R110, R111, R116: the machine holding a part's lease audits the segments its head reaches on schedule,
// heals a failed chain by snapshot, commits a repaired snapshot without dropping what it shares, re-issues spent
// challenges, and keeps the round in the head.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MeshAuditorFacts
{
    private static PartId Part => PartId.ForApp(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id, "audited");

    private static readonly TimeSpan AuditEvery = new MeshOptions().AuditEvery;

    // The trust host's node.audit spans, by the machine that reported.
    private sealed class Reports : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<string> _machines = new();

        public Reports()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == TrustLinkEndpoint.SourceName,
                Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.DisplayName == $"{nameof(TrustLinkEndpoint)} {TrustOperations.NodeAudit}" && activity.GetTagItem("digitalbrain.machine") is string machine)
                    { _machines.Enqueue(machine); }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public int From(ITestBrain machine) => _machines.Count(id => id == machine.SiloServices.GetRequiredService<IMachineTransport>().LocalMachineId);

        public void Dispose() => _listener.Dispose();
    }

    // A machine of the owner's account holding parts on the part log, its auditor on `clock`; not the core holder, so the
    // parts it holds are the ones a fact opens.
    private static Task<ITestBrain> Holder(TrustHost trust, FakeTimeProvider clock, CancellationToken ct, int challenges = 16)
        => TestBrain.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ChallengesPerShard)}", challenges.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureSilo(silo => silo.Services.AddKeyedSingleton<TimeProvider>(MeshOptions.Key, clock))
            .StartAsync(ct);

    private static async Task<PartLog> Write(ITestBrain holder, params byte[][] records)
    {
        var log = await holder.SiloServices.GetRequiredService<PartLogs>().For(Part);
        foreach (var record in records) { await log.Append($"counter/{Part.Value}/c1", "counter", RecordChange.Write, record, []); }
        await log.Flushed();
        return log;
    }

    private static async Task Pass(ITestBrain holder, FakeTimeProvider clock)
    {
        var pass = holder.SiloServices.GetRequiredService<MeshAuditor>().NextPass();
        clock.Advance(AuditEvery);
        await pass;
    }

    private static async Task<int> Refs(PartLog log)
    {
        var (snapshot, chain) = await log.Reached();
        return chain.Length + (snapshot is null ? 0 : 1);
    }

    private static async Task Stop(params IAsyncDisposable?[] hosts)
    {
        foreach (var host in hosts) { if (host is not null) { await host.DisposeAsync(); } }
    }

    [Fact(Timeout = 180_000)]
    public async Task TheHolderAuditsItsPartsSegmentsOnSchedule()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        using var reports = new Reports();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var trust = await TrustHost.StartAsync(ct);
        await using var p = await Holder(trust, clock, ct);
        var log = await Write(p, [1], [2]);
        var refs = await Refs(log);
        Assert.True(refs > 0);

        await Pass(p, clock);

        Assert.Equal(refs * 6, reports.From(p));
        Assert.Equal(1, log.Head.AuditRound);
        await Pass(p, clock);
        Assert.Equal(2 * refs * 6, reports.From(p));
        Assert.Equal(2, log.Head.AuditRound);
    }

    [Fact(Timeout = 240_000)]
    public async Task AFailedChainSegmentIsHealedBySnapshot()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var trust = await TrustHost.StartAsync(ct);
        ITestBrain? p = null, x = null, y = null, z = null;
        try
        {
            x = await VolunteerMeshFacts.NodeMachine(trust, "bob", ct, (options, cap) => new LyingShardStore(options, cap));
            (y, z) = (await VolunteerMeshFacts.NodeMachine(trust, "carol", ct), await VolunteerMeshFacts.NodeMachine(trust, "dave", ct));
            p = await Holder(trust, clock, ct);
            var log = await Write(p, [1], [2]);
            var (before, chain) = await log.Reached();
            Assert.NotEmpty(chain);
            var mesh = p.SiloServices.GetRequiredService<IMeshStore>();
            Assert.Contains((await VolunteerMeshFacts.Manifest(p, log.Key, chain[^1], ct)).Shards, shard => shard.NodeId == VolunteerMeshFacts.NodeOf(x));

            await Pass(p, clock);

            var (snapshot, healed) = await log.Reached();
            Assert.Empty(healed);
            Assert.Null(log.Head.Head);
            Assert.NotNull(snapshot);
            Assert.NotEqual(before?.ManifestCid, snapshot.ManifestCid);
            foreach (var old in chain) { await Assert.ThrowsAsync<ServiceUnavailableException>(() => mesh.Read(log.Key, old, ct)); }
            Assert.Equal(1, log.Head.AuditRound);
            Assert.Equal([2], log.Read($"counter/{Part.Value}/c1", "counter")!.Record);
        }
        finally { await Stop(p, z, y, x, trust); }
    }

    [Fact(Timeout = 240_000)]
    public async Task ARepairedSnapshotIsCommittedAndItsSharedShardsAreKept()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directoryClock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var trust = await TrustHost.StartAsync(ct, configure: silo => silo.Services.AddKeyedSingleton<TimeProvider>(MeshOptions.Key, directoryClock));
        ITestBrain? p = null, x = null, y = null, z = null, v = null;
        try
        {
            (x, y, z) = (await VolunteerMeshFacts.NodeMachine(trust, "bob", ct), await VolunteerMeshFacts.NodeMachine(trust, "carol", ct),
                await VolunteerMeshFacts.NodeMachine(trust, "dave", ct));
            p = await Holder(trust, clock, ct);
            var log = await Write(p, [1]);
            await log.HealBySnapshot();
            var (old, _) = await log.Reached();
            var shards = p.SiloServices.GetRequiredService<IShardClient>();
            var oldManifest = await VolunteerMeshFacts.Manifest(p, log.Key, old!, ct);
            var zs = VolunteerMeshFacts.NodeOf(z);
            var lost = Assert.Single(oldManifest.Shards, shard => shard.NodeId == zs);
            v = await VolunteerMeshFacts.NodeMachine(trust, "erin", ct);
            await z.DisposeAsync();
            z = null;
            directoryClock.Advance(new MeshOptions().OfflineAfter + TimeSpan.FromSeconds(1));
            foreach (var beating in (ITestBrain[])[x, y, v]) { await beating.SiloServices.GetRequiredService<StorageNodeService>().Heartbeat(); }

            await Pass(p, clock);

            var (repaired, _) = await log.Reached();
            Assert.NotEqual(old!.ManifestCid, repaired!.ManifestCid);
            var manifest = await VolunteerMeshFacts.Manifest(p, log.Key, repaired, ct);
            Assert.Equal(VolunteerMeshFacts.NodeOf(v), manifest.Shards[lost.Index].NodeId);
            foreach (var shard in manifest.Shards.Where(shard => shard.Index != lost.Index))
            {
                Assert.Equal((oldManifest.Shards[shard.Index].NodeId, oldManifest.Shards[shard.Index].Cid), (shard.NodeId, shard.Cid));
                Assert.NotNull(await shards.Get(shard.NodeId, shard.Cid, ct));
            }
            foreach (var node in old.ManifestNodes.Except(repaired.ManifestNodes)) { Assert.Null(await shards.Get(node, old.ManifestCid, ct)); }
            Assert.Equal([1], p.SiloServices.GetRequiredService<Orleans.Serialization.Serializer>()
                .Deserialize<PartSnapshot>(await p.SiloServices.GetRequiredService<IMeshStore>().Read(log.Key, repaired, ct))!.Records.Single().Record);
            Assert.Equal(1, log.Head.AuditRound);
        }
        finally { await Stop(p, v, z, y, x, trust); }
    }

    [Fact(Timeout = 180_000)]
    public async Task ANonHolderNeverAudits()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        using var reports = new Reports();
        var (clock, other) = (new FakeTimeProvider(DateTimeOffset.UtcNow), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var trust = await TrustHost.StartAsync(ct);
        await using var p = await Holder(trust, clock, ct);
        await using var q = await Holder(trust, other, ct);
        var log = await Write(p, [1]);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => q.SiloServices.GetRequiredService<PartLogs>().For(Part));

        await Pass(q, other);

        Assert.Equal(0, reports.From(q));
        Assert.Equal(0, log.Head.AuditRound);
    }

    [Fact(Timeout = 180_000)]
    public async Task ExhaustedChallengesAreReissuedNotReportedFailed()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        using var reports = new Reports();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var trust = await TrustHost.StartAsync(ct);
        await using var p = await Holder(trust, clock, ct, challenges: 1);
        var log = await Write(p, [1], [2]);
        await Pass(p, clock);
        var audited = reports.From(p);
        Assert.True(audited > 0);
        Assert.Equal(1, log.Head.AuditRound);
        var (before, _) = await log.Reached();

        await Pass(p, clock);

        Assert.Equal(audited, reports.From(p));
        Assert.Equal(0, log.Head.AuditRound);
        var (reissued, chain) = await log.Reached();
        Assert.Empty(chain);
        Assert.NotEqual(before?.ManifestCid, reissued!.ManifestCid);
        Assert.All(await p.SiloServices.GetRequiredService<IMeshStore>().Audit(log.Key, reissued, 0, ct), result => Assert.True(result.Passed, result.Reason));
    }

    [Fact(Timeout = 180_000)]
    public async Task TheAuditRoundSurvivesAHolderChange()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        using var reports = new Reports();
        var (clock, other) = (new FakeTimeProvider(DateTimeOffset.UtcNow), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var trust = await TrustHost.StartAsync(ct);
        await using var p = await Holder(trust, clock, ct);
        await using var q = await Holder(trust, other, ct);
        await Write(p, [1]);
        await Pass(p, clock);
        await p.SiloServices.GetRequiredService<PartLogs>().Release(Part);

        var log = await q.SiloServices.GetRequiredService<PartLogs>().For(Part);
        Assert.Equal(1, log.Head.AuditRound);
        await Pass(q, other);

        Assert.Equal(2, log.Head.AuditRound);
        Assert.Equal(await Refs(log) * 6, reports.From(q));
    }
}
