using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IntoChat.OS.Modules.Microsoft.Orleans.Cluster;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

public sealed class MembershipFeedFacts
{
    [Fact]
    public async Task CurrentAndChangesComeFromTheRuntimeStreamWithoutRefreshRpc()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new MembershipSource();
        var feed = new ClusterMembershipFeed(runtime, NullLogger<ClusterMembershipFeed>.Instance);
        var observed = Channel.CreateUnbounded<ClusterSnapshot?>();
        await using var subscription = feed.Subscribe(value => { observed.Writer.TryWrite(value); return Task.CompletedTask; });
        Assert.Equal("Active", Assert.Single(feed.Current.Silos).Status);
        Assert.Equal("Active", Assert.Single((await observed.Reader.ReadAsync(ct))!.Silos).Status);
        runtime.Send(SiloStatus.Active); // A newer table version with identical membership is silent.
        runtime.Send(SiloStatus.ShuttingDown);
        Assert.Equal("ShuttingDown", Assert.Single((await observed.Reader.ReadAsync(ct))!.Silos).Status);
        Assert.False(observed.Reader.TryRead(out _));
        Assert.Equal(0, runtime.Refreshes);
    }

    [Fact]
    public async Task DisposingReleasesAQueuedGrainCallbackAndItsRuntimeEnumerator()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new MembershipSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new ClusterMembershipFeed(runtime, NullLogger<ClusterMembershipFeed>.Instance);
        var subscription = feed.Subscribe(_ => { entered.TrySetResult(); return held.Task; });
        await entered.Task.WaitAsync(ct);
        await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(0, runtime.Enumerators);
        held.TrySetResult();
    }

    [Fact]
    public async Task AFailedStreamReportsUnavailableThenSeedsTheRecoveredSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new MembershipSource { FailFirst = true };
        var observed = Channel.CreateUnbounded<ClusterSnapshot?>();
        var feed = new ClusterMembershipFeed(runtime, NullLogger<ClusterMembershipFeed>.Instance);
        await using var subscription = feed.Subscribe(value => { observed.Writer.TryWrite(value); return Task.CompletedTask; });
        Assert.Null(await observed.Reader.ReadAsync(ct));
        Assert.Equal("Active", Assert.Single((await observed.Reader.ReadAsync(ct))!.Silos).Status);
    }

    private sealed class MembershipSource : IClusterMembershipService
    {
        private readonly Channel<ClusterMembershipSnapshot> updates = Channel.CreateUnbounded<ClusterMembershipSnapshot>();
        private static readonly SiloAddress Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
        public ClusterMembershipSnapshot CurrentSnapshot { get; private set; } = Snapshot(SiloStatus.Active, 1);
        public int Refreshes { get; private set; }
        public int Enumerators { get; private set; }
        public bool FailFirst { get; set; }
        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => Read();
        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        { Refreshes++; return ValueTask.CompletedTask; }
        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotSupportedException();
        public void Send(SiloStatus status)
        {
            CurrentSnapshot = Snapshot(status, CurrentSnapshot.Version.Value + 1);
            updates.Writer.TryWrite(CurrentSnapshot);
        }
        private static ClusterMembershipSnapshot Snapshot(SiloStatus status, long version)
            => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(Address, new ClusterMember(Address, status, "silo")), new MembershipVersion(version));
        private async IAsyncEnumerable<ClusterMembershipSnapshot> Read([EnumeratorCancellation] CancellationToken ct = default)
        {
            Enumerators++;
            try
            {
                if (FailFirst) { FailFirst = false; throw new IOException("membership unavailable"); }
                yield return CurrentSnapshot;
                await foreach (var snapshot in updates.Reader.ReadAllAsync(ct)) { yield return snapshot; }
            }
            finally { Enumerators--; }
        }
    }
}
