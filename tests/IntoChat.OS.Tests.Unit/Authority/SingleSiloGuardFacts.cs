using System.Collections.Immutable;
using System.Net;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class SingleSiloGuardFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SiloAddress NewSilo(int port, int generation)
        => SiloAddress.New(IPAddress.Loopback, port, generation);

    private static ClusterMember Member(SiloAddress address, SiloStatus status, string name)
        => new(address, status, name);

    [Fact]
    public void Participate_RegistersCheckStrictlyBeforeBecomeActive()
    {
        var self = NewSilo(11111, 1);
        var membership = new FakeMembershipService(Snapshot(1, Member(self, SiloStatus.Joining, "self")));
        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "test-cluster" }),
            NullLogger<SingleSiloGuard>.Instance);

        var lifecycle = new FakeSiloLifecycle();
        guard.Participate(lifecycle);

        Assert.Equal(ServiceLifecycleStage.BecomeActive - 1, SingleSiloGuard.Stage);
        Assert.Contains(lifecycle.Stages, stage => stage == SingleSiloGuard.Stage);
        Assert.Contains(lifecycle.Stages, stage => stage == ServiceLifecycleStage.Active);
    }

    [Fact]
    public async Task AloneInCluster_SucceedsImmediatelyWithoutSelfCleanup()
    {
        var self = NewSilo(11111, 2);
        var deadPeer = NewSilo(11111, 1);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(deadPeer, SiloStatus.Dead, "old")));
        var table = new FakeMembershipTable(self, SiloStatus.Joining);

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "test-cluster" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.FromSeconds(30), PollInterval = TimeSpan.FromMilliseconds(20) }));

        await RunCheckStageAsync(guard, Ct);

        Assert.Equal(1, membership.RefreshCount);
        Assert.Equal(SiloStatus.Joining, table.StatusOf(self));
        Assert.Equal(0, table.UpdateCalls);
    }

    [Fact]
    public async Task PeerLeavesWithinGracePeriod_WaitsThroughShuttingDownAndStopping_AndSucceedsOnceDead()
    {
        var self = NewSilo(11111, 2);
        var oldPeer = NewSilo(11111, 1);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(oldPeer, SiloStatus.Active, "old")),
            Snapshot(2, Member(self, SiloStatus.Joining, "self"), Member(oldPeer, SiloStatus.ShuttingDown, "old")),
            Snapshot(3, Member(self, SiloStatus.Joining, "self"), Member(oldPeer, SiloStatus.Stopping, "old")),
            Snapshot(4, Member(self, SiloStatus.Joining, "self"), Member(oldPeer, SiloStatus.Dead, "old")));
        var table = new FakeMembershipTable(self, SiloStatus.Joining);

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "test-cluster" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(20) }));

        await RunCheckStageAsync(guard, Ct);

        Assert.Equal(4, membership.RefreshCount);
        Assert.Equal(SiloStatus.Joining, table.StatusOf(self));
        Assert.Equal(0, table.UpdateCalls);
    }

    [Theory]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    public async Task PeerStaysNonDeadPastGracePeriod_RefusesFailClosedAndMarksOnlySelfDead(SiloStatus peerStatus)
    {
        var self = NewSilo(11111, 2);
        var peer = NewSilo(11111, 1);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(peer, peerStatus, "peer")));
        var table = new FakeMembershipTable(self, SiloStatus.Joining);
        // Even if the peer's IAmAliveTime in the table is hours old, SingleSiloGuard stays strictly fail-closed.
        table.AddRow(peer, peerStatus, DateTime.UtcNow.AddHours(-3));

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.FromMilliseconds(80), PollInterval = TimeSpan.FromMilliseconds(20) }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.Contains("must be the only silo of its cluster", error.Message, StringComparison.Ordinal);
        Assert.Contains("intochat-test", error.Message, StringComparison.Ordinal);
        Assert.Contains(peer.ToString(), error.Message, StringComparison.Ordinal);
        Assert.True(membership.RefreshCount >= 2, $"Expected polling during grace period, saw {membership.RefreshCount} refresh(es).");
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Equal(peerStatus, table.StatusOf(peer));
        Assert.Equal(1, table.UpdateCalls);
    }

    [Fact]
    public void ResolveGracePeriod_CapsStrictlyBelowMaxJoinAttemptTimeAndStartupBudget()
    {
        var acaStartupProbeBudget = TimeSpan.FromSeconds(10 * 30);
        var prodMaxJoin = TimeSpan.FromMinutes(2);

        Assert.True(SingleSiloGuardOptions.MaxGracePeriod < prodMaxJoin);
        Assert.True(SingleSiloGuardOptions.MaxGracePeriod < acaStartupProbeBudget);
        Assert.Equal(TimeSpan.FromSeconds(30), SingleSiloGuard.ResolveGracePeriod(TimeSpan.FromSeconds(30), prodMaxJoin));
        Assert.Equal(SingleSiloGuardOptions.MaxGracePeriod, SingleSiloGuard.ResolveGracePeriod(TimeSpan.FromMinutes(10), prodMaxJoin));
        Assert.Equal(TimeSpan.FromSeconds(30), SingleSiloGuard.ResolveGracePeriod(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromSeconds(5), SingleSiloGuard.ResolveGracePeriod(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, SingleSiloGuard.ResolveGracePeriod(TimeSpan.Zero, prodMaxJoin));
        Assert.Equal(TimeSpan.Zero, SingleSiloGuard.ResolveGracePeriod(TimeSpan.FromSeconds(-5), prodMaxJoin));
    }

    [Fact]
    public async Task OptionsExposeNoDisableFlag_AndZeroGraceStillRefusesFailClosed()
    {
        var propertyNames = typeof(SingleSiloGuardOptions).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(propertyNames, name => name.Contains("Enable", StringComparison.OrdinalIgnoreCase) || name.Contains("Disable", StringComparison.OrdinalIgnoreCase));

        var self = NewSilo(11111, 2);
        var peer = NewSilo(11111, 1);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(peer, SiloStatus.Active, "peer")));
        var table = new FakeMembershipTable(self, SiloStatus.Joining);

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));
        Assert.Equal(1, membership.RefreshCount);
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
    }

    [Fact]
    public async Task SelfCleanupFailure_NeverHidesRefusalException()
    {
        var self = NewSilo(11111, 2);
        var peer = NewSilo(11111, 1);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(peer, SiloStatus.Active, "peer")));
        var failingTable = new FakeMembershipTable(self, SiloStatus.Joining) { ThrowOnRead = true };

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            failingTable,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.Contains("must be the only silo of its cluster", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringGracePeriod_StopsWaitingPromptly()
    {
        var self = NewSilo(11111, 2);
        var peer = NewSilo(11111, 1);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(peer, SiloStatus.Active, "peer")));

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            options: Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.FromSeconds(30), PollInterval = TimeSpan.FromSeconds(5) }));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCheckStageAsync(guard, cts.Token));
    }

    private static async Task RunCheckStageAsync(SingleSiloGuard guard, CancellationToken ct)
    {
        var lifecycle = new FakeSiloLifecycle();
        guard.Participate(lifecycle);
        await lifecycle.StartStageAsync(SingleSiloGuard.Stage, ct);
    }

    private static ClusterMembershipSnapshot Snapshot(long version, params ClusterMember[] members)
        => new(members.ToImmutableDictionary(m => m.SiloAddress), new MembershipVersion(version));

    private sealed class FakeSiloLifecycle : ISiloLifecycle
    {
        private readonly List<(int Stage, ILifecycleObserver Observer)> _observers = [];

        public IEnumerable<int> Stages => _observers.Select(item => item.Stage);

        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => int.MaxValue;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            _observers.Add((stage, observer));
            return new NoopDisposable();
        }

        public async Task StartStageAsync(int stage, CancellationToken ct)
        {
            foreach (var (_, observer) in _observers.Where(item => item.Stage == stage))
            {
                await observer.OnStart(ct);
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeLocalSiloDetails(SiloAddress address, string name) : ILocalSiloDetails
    {
        public string Name => name;
        public string ClusterId => "test-cluster";
        public string DnsHostName => "localhost";
        public SiloAddress SiloAddress => address;
        public SiloAddress GatewayAddress => null!;
    }

    private sealed class FakeMembershipService(params ClusterMembershipSnapshot[] sequence) : IClusterMembershipService
    {
        private int _index = -1;

        public int RefreshCount { get; private set; }

        public ClusterMembershipSnapshot CurrentSnapshot => sequence[Math.Clamp(_index, 0, sequence.Length - 1)];

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => EmptyUpdates();

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            if (_index + 1 < sequence.Length)
            {
                _index++;
            }

            return ValueTask.CompletedTask;
        }

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        private static async IAsyncEnumerable<ClusterMembershipSnapshot> EmptyUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeMembershipTable : IMembershipTable
    {
        private readonly Dictionary<SiloAddress, (MembershipEntry Entry, string ETag)> _rows = [];
        private int _version = 1;

        public FakeMembershipTable(SiloAddress self, SiloStatus initialStatus)
        {
            AddRow(self, initialStatus, DateTime.UtcNow);
        }

        public bool ThrowOnRead { get; set; }

        public int UpdateCalls { get; private set; }

        public void AddRow(SiloAddress address, SiloStatus status, DateTime iAmAliveTime)
        {
            _rows[address] = (
                new MembershipEntry
                {
                    SiloAddress = address,
                    SiloName = address.ToString(),
                    HostName = "localhost",
                    Status = status,
                    StartTime = iAmAliveTime,
                    IAmAliveTime = iAmAliveTime,
                    SuspectTimes = [],
                },
                $"etag-{_version}");
        }

        public SiloStatus StatusOf(SiloAddress address) => _rows[address].Entry.Status;

        public Task InitializeMembershipTable(bool tryInitTableVersion) => Task.CompletedTask;

        public Task DeleteMembershipTableEntries(string clusterId) => Task.CompletedTask;

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => Task.CompletedTask;

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated membership store outage.");
            }

            var tableVersion = new TableVersion(_version, $"v-{_version}");
            if (_rows.TryGetValue(key, out var row))
            {
                return Task.FromResult(new MembershipTableData(Tuple.Create(row.Entry, row.ETag), tableVersion));
            }

            return Task.FromResult(new MembershipTableData(tableVersion));
        }

        public Task<MembershipTableData> ReadAll()
        {
            var tableVersion = new TableVersion(_version, $"v-{_version}");
            var list = _rows.Values.Select(row => Tuple.Create(row.Entry, row.ETag)).ToList();
            return Task.FromResult(new MembershipTableData(list, tableVersion));
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            _version = tableVersion.Version;
            _rows[entry.SiloAddress] = (entry, $"etag-{_version}");
            return Task.FromResult(true);
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            UpdateCalls++;
            if (!_rows.TryGetValue(entry.SiloAddress, out var current) || current.ETag != etag)
            {
                return Task.FromResult(false);
            }

            _version = tableVersion.Version;
            _rows[entry.SiloAddress] = (entry, $"etag-{_version}");
            return Task.FromResult(true);
        }

        public Task UpdateIAmAlive(MembershipEntry entry) => Task.CompletedTask;
    }
}
