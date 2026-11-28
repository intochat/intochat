using System.Collections.Immutable;
using System.Net;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class SingleSiloGuardFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IOptions<ClusterMembershipOptions> FastConfirmOptions => Options.Create(new ClusterMembershipOptions
    {
        IAmAliveTablePublishTimeout = TimeSpan.FromMilliseconds(30),
        NumMissedTableIAmAliveLimit = 2,
    });

    private static readonly Func<IPEndPoint, CancellationToken, Task<bool>> UnreachableProbe = static (_, _) => Task.FromResult(false);
    private static readonly Func<IPEndPoint, CancellationToken, Task<bool>> ReachableProbe = static (_, _) => Task.FromResult(true);

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
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var membership = new FakeMembershipService(
            Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(peer, peerStatus, "peer")));
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        // Live (fresh heartbeat within threshold) peer must remain untouched and SingleSiloGuard must refuse fail-closed.
        table.AddRow(peer, peerStatus, now.UtcDateTime.AddSeconds(-10));

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.FromMilliseconds(80), PollInterval = TimeSpan.FromMilliseconds(20) }),
            timeProvider: clock);

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
    public void ResolveStalePeerThreshold_Enforces60sFloorAndHonorsLargerMembershipIntervals()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SingleSiloGuard.ResolveStalePeerThreshold(null));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            SingleSiloGuard.ResolveStalePeerThreshold(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10),
                NumMissedTableIAmAliveLimit = 2,
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(180),
            SingleSiloGuard.ResolveStalePeerThreshold(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(30),
                NumMissedTableIAmAliveLimit = 3,
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(100),
            SingleSiloGuard.ResolveStalePeerThreshold(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(20),
                NumMissedTableIAmAliveLimit = 2,
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            SingleSiloGuard.ResolveStalePeerThreshold(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.Zero,
                NumMissedTableIAmAliveLimit = 5,
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            SingleSiloGuard.ResolveStalePeerThreshold(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(-5),
                NumMissedTableIAmAliveLimit = 5,
            }));
    }

    [Fact]
    public void ResolveHeartbeatConfirmDelay_UsesPublishTimeoutOrDefault10s()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), SingleSiloGuard.ResolveHeartbeatConfirmDelay(null));
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            SingleSiloGuard.ResolveHeartbeatConfirmDelay(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(10),
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            SingleSiloGuard.ResolveHeartbeatConfirmDelay(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(30),
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            SingleSiloGuard.ResolveHeartbeatConfirmDelay(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.Zero,
            }));
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            SingleSiloGuard.ResolveHeartbeatConfirmDelay(new ClusterMembershipOptions
            {
                IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(-2),
            }));
    }

    [Theory]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    public async Task StaleZombiePeer_IsMarkedDeadWithETagAndAllowsStartup(SiloStatus peerStatus)
    {
        var self = NewSilo(11111, 2);
        var zombie = NewSilo(11112, 1);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var staleTime = now.UtcDateTime.AddSeconds(-61);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        table.AddRow(zombie, peerStatus, staleTime);
        var membership = new TableBackedMembershipService(table);
        IPEndPoint? probedEndpoint = null;

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = (ep, _) =>
            {
                probedEndpoint = ep;
                return Task.FromResult(false);
            },
        };

        await RunCheckStageAsync(guard, Ct);

        Assert.Equal(zombie.Endpoint, probedEndpoint);
        Assert.Equal(SiloStatus.Dead, table.StatusOf(zombie));
        Assert.Equal(SiloStatus.Joining, table.StatusOf(self));
        Assert.Equal(staleTime, table.EntryOf(zombie).IAmAliveTime);
        var suspects = Assert.IsType<List<Tuple<SiloAddress, DateTime>>>(table.EntryOf(zombie).SuspectTimes);
        Assert.Contains(suspects, suspect => suspect.Item1.Equals(self));
        Assert.Equal(1, table.UpdateCalls);
        Assert.Equal(2, membership.RefreshCount);
    }

    [Fact]
    public async Task StalePeerWithReachableTcpEndpoint_RefusesFailClosedWithoutMarkingDead()
    {
        var self = NewSilo(11111, 2);
        var partitionedLivePeer = NewSilo(11112, 1);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        // Heartbeat is 5 minutes stale and non-advancing, but the peer's TCP silo port is still listening.
        table.AddRow(partitionedLivePeer, SiloStatus.Active, now.UtcDateTime.AddMinutes(-5));
        var membership = new TableBackedMembershipService(table);

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = ReachableProbe,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.Contains(partitionedLivePeer.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(SiloStatus.Active, table.StatusOf(partitionedLivePeer));
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Equal(1, table.UpdateCalls); // Only self cleanup, never touched peer.
    }

    [Fact]
    public async Task StalePeerAdvancingHeartbeatDuringConfirmWait_RefusesFailClosedWithoutMarkingDead()
    {
        var self = NewSilo(11111, 2);
        var skewedLivePeer = NewSilo(11112, 1);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        // Simulate clock skew: peer timestamp looks 5m stale, but advances by 10s during the IAmAliveTablePublishTimeout wait.
        table.AddRow(skewedLivePeer, SiloStatus.Active, now.UtcDateTime.AddMinutes(-5));
        var peerReads = 0;
        table.OnAfterRead = address =>
        {
            if (address.Equals(skewedLivePeer) && ++peerReads == 1)
            {
                table.AddRow(skewedLivePeer, SiloStatus.Active, now.UtcDateTime.AddMinutes(-5).AddSeconds(10));
            }
        };
        var membership = new TableBackedMembershipService(table);
        var probeCalled = false;

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = (_, _) =>
            {
                probeCalled = true;
                return Task.FromResult(false);
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.False(probeCalled);
        Assert.Equal(SiloStatus.Active, table.StatusOf(skewedLivePeer));
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Equal(1, table.UpdateCalls); // Only self cleanup.
    }

    [Fact]
    public async Task StaleZombiePeerWithDefaultIAmAliveTime_FallsBackToStartTime()
    {
        var self = NewSilo(11111, 2);
        var zombie = NewSilo(11112, 1);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);

        // Default IAmAliveTime + stale StartTime + failed TCP probe -> reaped and allowed to start.
        var staleTable = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        staleTable.AddRow(zombie, SiloStatus.Active, startTime: now.UtcDateTime.AddSeconds(-90), iAmAliveTime: default);
        var staleMembership = new TableBackedMembershipService(staleTable);
        var staleGuard = new SingleSiloGuard(
            staleMembership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            staleTable,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        await RunCheckStageAsync(staleGuard, Ct);
        Assert.Equal(SiloStatus.Dead, staleTable.StatusOf(zombie));
        Assert.Equal(SiloStatus.Joining, staleTable.StatusOf(self));

        // Default IAmAliveTime + fresh StartTime -> fail-closed.
        var freshTable = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        freshTable.AddRow(zombie, SiloStatus.Active, startTime: now.UtcDateTime.AddSeconds(-10), iAmAliveTime: default);
        var freshMembership = new TableBackedMembershipService(freshTable);
        var freshGuard = new SingleSiloGuard(
            freshMembership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            freshTable,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(freshGuard, Ct));
        Assert.Equal(SiloStatus.Active, freshTable.StatusOf(zombie));
        Assert.Equal(SiloStatus.Dead, freshTable.StatusOf(self));

        // Both IAmAliveTime and StartTime default -> fail-closed (no proof of staleness).
        var uninitializedTable = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        uninitializedTable.AddRow(zombie, SiloStatus.Active, startTime: default, iAmAliveTime: default);
        var uninitializedMembership = new TableBackedMembershipService(uninitializedTable);
        var uninitializedGuard = new SingleSiloGuard(
            uninitializedMembership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            uninitializedTable,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(uninitializedGuard, Ct));
        Assert.Equal(SiloStatus.Active, uninitializedTable.StatusOf(zombie));
        Assert.Equal(SiloStatus.Dead, uninitializedTable.StatusOf(self));
    }

    [Fact]
    public async Task StalePeerETagContention_RetriesWhenStillStale_AndAbortsIfPeerBecomesFresh()
    {
        var self = NewSilo(11111, 2);
        var peer = NewSilo(11112, 1);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);

        // Case 1: Concurrent ETag bump while peer stays stale -> retries with updated ETag and succeeds.
        var retryTable = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        retryTable.AddRow(peer, SiloStatus.Active, now.UtcDateTime.AddSeconds(-90));
        var bumpedOnce = false;
        retryTable.OnBeforeUpdate = address =>
        {
            if (!bumpedOnce && address.Equals(peer))
            {
                bumpedOnce = true;
                retryTable.BumpETag(peer);
            }
        };
        var retryMembership = new TableBackedMembershipService(retryTable);
        var retryGuard = new SingleSiloGuard(
            retryMembership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            retryTable,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        await RunCheckStageAsync(retryGuard, Ct);
        Assert.Equal(SiloStatus.Dead, retryTable.StatusOf(peer));
        Assert.Equal(2, retryTable.UpdateCalls);

        // Case 2: Peer wakes up and refreshes IAmAliveTime concurrently (bumping ETag) -> retry sees fresh heartbeat and refuses.
        var revivedTable = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        revivedTable.AddRow(peer, SiloStatus.Active, now.UtcDateTime.AddSeconds(-90));
        var revivedOnce = false;
        revivedTable.OnBeforeUpdate = address =>
        {
            if (!revivedOnce && address.Equals(peer))
            {
                revivedOnce = true;
                revivedTable.AddRow(peer, SiloStatus.Active, now.UtcDateTime);
                revivedTable.BumpETag(peer);
            }
        };
        var revivedMembership = new TableBackedMembershipService(revivedTable);
        var revivedGuard = new SingleSiloGuard(
            revivedMembership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            revivedTable,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(revivedGuard, Ct));
        Assert.Equal(SiloStatus.Active, revivedTable.StatusOf(peer));
        Assert.Equal(SiloStatus.Dead, revivedTable.StatusOf(self));
    }

    [Fact]
    public async Task PeerBecomesStaleDuringGracePeriod_IsReapedWithoutRefusal()
    {
        var self = NewSilo(11111, 2);
        var peer = NewSilo(11112, 1);
        var start = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(start);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, start.UtcDateTime);
        // 55s old at check start (not yet stale > 60s), then crosses 60s during grace polling.
        table.AddRow(peer, SiloStatus.Active, start.UtcDateTime.AddSeconds(-55));
        var membership = new TableBackedMembershipService(table, onRefresh: count =>
        {
            if (count == 2)
            {
                clock.AdvanceUtcNow(TimeSpan.FromSeconds(10));
            }
        });

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(20) }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        await RunCheckStageAsync(guard, Ct);

        Assert.Equal(SiloStatus.Dead, table.StatusOf(peer));
        Assert.Equal(SiloStatus.Joining, table.StatusOf(self));
        Assert.True(membership.RefreshCount >= 3);
    }

    [Fact]
    public async Task MixedStaleAndLivePeers_ReapsOnlyStalePeerAndStillRefusesForLivePeer()
    {
        var self = NewSilo(11111, 3);
        var zombie = NewSilo(11112, 1);
        var live = NewSilo(11113, 2);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        table.AddRow(zombie, SiloStatus.Active, now.UtcDateTime.AddSeconds(-120));
        table.AddRow(live, SiloStatus.Active, now.UtcDateTime.AddSeconds(-5));
        var membership = new TableBackedMembershipService(table);

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = UnreachableProbe,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.Equal(SiloStatus.Dead, table.StatusOf(zombie));
        Assert.Equal(SiloStatus.Active, table.StatusOf(live));
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Contains(live.ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(zombie.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleSameNonLoopbackEndpointOlderGeneration_SkipsTcpSelfProbeAndReapsOrphan()
    {
        var podIp = IPAddress.Parse("100.100.207.145");
        var self = SiloAddress.New(podIp, 11111, 200);
        var priorProcess = SiloAddress.New(podIp, 11111, 100);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var staleTime = now.UtcDateTime.AddSeconds(-90);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        table.AddRow(priorProcess, SiloStatus.Active, staleTime);
        var membership = new TableBackedMembershipService(table);
        var probeCalled = false;

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            // If the TCP probe were invoked against this same endpoint, it would connect to self and return true.
            PeerEndpointProbe = (_, _) =>
            {
                probeCalled = true;
                return Task.FromResult(true);
            },
        };

        await RunCheckStageAsync(guard, Ct);

        Assert.False(probeCalled, "TCP self-probe must be skipped for a stale older-generation peer on the same non-loopback endpoint.");
        Assert.Equal(SiloStatus.Dead, table.StatusOf(priorProcess));
        Assert.Equal(SiloStatus.Joining, table.StatusOf(self));
        Assert.Equal(staleTime, table.EntryOf(priorProcess).IAmAliveTime);
        var suspects = Assert.IsType<List<Tuple<SiloAddress, DateTime>>>(table.EntryOf(priorProcess).SuspectTimes);
        Assert.Contains(suspects, suspect => suspect.Item1.Equals(self));
        Assert.Equal(1, table.UpdateCalls);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task StaleSameLoopbackEndpoint_RefusesFailClosedWithoutProbingOrReaping(string loopbackAddress)
    {
        var ip = IPAddress.Parse(loopbackAddress);
        var self = SiloAddress.New(ip, 11111, 200);
        var peer = SiloAddress.New(ip, 11111, 100);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        table.AddRow(peer, SiloStatus.Active, now.UtcDateTime.AddSeconds(-120));
        var membership = new TableBackedMembershipService(table);
        var probeCalled = false;

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = (_, _) =>
            {
                probeCalled = true;
                return Task.FromResult(false);
            },
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.False(probeCalled);
        Assert.Contains(peer.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(SiloStatus.Active, table.StatusOf(peer));
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Equal(1, table.UpdateCalls); // Only self cleanup, peer untouched.
    }

    [Theory]
    [InlineData(201)]
    [InlineData(500)]
    public async Task StaleSameNonLoopbackEndpointNewerGeneration_RefusesFailClosedWithoutProbingOrReaping(int peerGeneration)
    {
        var podIp = IPAddress.Parse("100.100.207.145");
        var self = SiloAddress.New(podIp, 11111, 200);
        var newerPeer = SiloAddress.New(podIp, 11111, peerGeneration);
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        table.AddRow(newerPeer, SiloStatus.Active, now.UtcDateTime.AddSeconds(-120));
        var membership = new TableBackedMembershipService(table);
        var probeCalled = false;

        var guard = new SingleSiloGuard(
            membership,
            new FakeLocalSiloDetails(self, "self"),
            Options.Create(new ClusterOptions { ClusterId = "intochat-test" }),
            NullLogger<SingleSiloGuard>.Instance,
            table,
            Options.Create(new SingleSiloGuardOptions { GracePeriod = TimeSpan.Zero }),
            FastConfirmOptions,
            clock)
        {
            PeerEndpointProbe = (_, _) =>
            {
                probeCalled = true;
                return Task.FromResult(false);
            },
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.False(probeCalled);
        Assert.Contains(newerPeer.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(SiloStatus.Active, table.StatusOf(newerPeer));
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Equal(1, table.UpdateCalls); // Only self cleanup, peer untouched.
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

    [Fact]
    public async Task Register_WrapsFatalErrorHandlerIdempotently_SuppressesOnlyDuringRefusal_AndForwardsOtherwise()
    {
        var self = NewSilo(11111, 2);
        var peer = NewSilo(11111, 1);
        var now = new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var table = new FakeMembershipTable(self, SiloStatus.Joining, now.UtcDateTime);
        table.AddRow(peer, SiloStatus.Active, now.UtcDateTime.AddSeconds(-5));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IFatalErrorHandler, RecordingFatalErrorHandler>();
        services.AddSingleton<IClusterMembershipService>(
            new FakeMembershipService(Snapshot(1, Member(self, SiloStatus.Joining, "self"), Member(peer, SiloStatus.Active, "peer"))));
        services.AddSingleton<ILocalSiloDetails>(new FakeLocalSiloDetails(self, "self"));
        services.AddSingleton<IMembershipTable>(table);
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<ClusterOptions>(o => o.ClusterId = "intochat-test");
        services.Configure<SingleSiloGuardOptions>(o => o.GracePeriod = TimeSpan.Zero);

        // Register twice (matching PlatformHosting + BrainHosting) to verify idempotent wrapping.
        SingleSiloGuard.Register(services);
        SingleSiloGuard.Register(services);

        await using var provider = services.BuildServiceProvider();
        var fatalHandler = provider.GetRequiredService<IFatalErrorHandler>();
        var inner = provider.GetRequiredService<RecordingFatalErrorHandler>();
        var refusalState = provider.GetRequiredService<SingleSiloRefusalState>();
        var guard = Assert.IsType<SingleSiloGuard>(Assert.Single(provider.GetServices<ILifecycleParticipant<ISiloLifecycle>>()));

        // Active / non-refusing silo: fatal errors must still delegate directly to Orleans's FatalErrorHandler.
        Assert.False(refusalState.IsRefusingStartup);
        fatalHandler.OnFatalException(this, "active-silo-fatal", null);
        Assert.Equal(1, inner.Calls);

        // Refusal state must be set strictly BEFORE MarkSelfDeadAsync writes Dead to the membership table.
        var refusalFlagObservedBeforeSelfDeadWrite = false;
        table.OnBeforeUpdate = address =>
        {
            if (address.Equals(self))
            {
                refusalFlagObservedBeforeSelfDeadWrite = refusalState.IsRefusingStartup;
                // Simulate a racing MembershipTableManager.PeriodicallyRefreshMembershipTable tick calling FailFast.
                fatalHandler.OnFatalException(this, "racing-refresh-saw-self-dead", null);
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCheckStageAsync(guard, Ct));

        Assert.True(refusalFlagObservedBeforeSelfDeadWrite);
        Assert.True(refusalState.IsRefusingStartup);
        Assert.Equal(SiloStatus.Dead, table.StatusOf(self));
        Assert.Equal(1, inner.Calls);
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void AdvanceUtcNow(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class TableBackedMembershipService(FakeMembershipTable table, Action<int>? onRefresh = null) : IClusterMembershipService
    {
        private ClusterMembershipSnapshot _current = new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue);

        public int RefreshCount { get; private set; }

        public ClusterMembershipSnapshot CurrentSnapshot => _current;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => EmptyUpdates();

        public async ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            onRefresh?.Invoke(RefreshCount);
            var data = await table.ReadAll().ConfigureAwait(false);
            var members = data.Members
                .Select(pair => new ClusterMember(pair.Item1.SiloAddress, pair.Item1.Status, pair.Item1.SiloName))
                .ToImmutableDictionary(m => m.SiloAddress);
            _current = new ClusterMembershipSnapshot(members, new MembershipVersion(data.Version.Version));
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

        public FakeMembershipTable(SiloAddress self, SiloStatus initialStatus, DateTime? timestamp = null)
        {
            AddRow(self, initialStatus, timestamp ?? DateTime.UtcNow);
        }

        public bool ThrowOnRead { get; set; }

        public int UpdateCalls { get; private set; }

        public Action<SiloAddress>? OnBeforeUpdate { get; set; }

        public Action<SiloAddress>? OnAfterRead { get; set; }

        public void AddRow(SiloAddress address, SiloStatus status, DateTime iAmAliveTime)
            => AddRow(address, status, startTime: iAmAliveTime, iAmAliveTime: iAmAliveTime);

        public void AddRow(SiloAddress address, SiloStatus status, DateTime startTime, DateTime iAmAliveTime)
        {
            _rows[address] = (
                new MembershipEntry
                {
                    SiloAddress = address,
                    SiloName = address.ToString(),
                    HostName = "localhost",
                    Status = status,
                    StartTime = startTime,
                    IAmAliveTime = iAmAliveTime,
                    SuspectTimes = [],
                },
                $"etag-{_version}");
        }

        public void BumpETag(SiloAddress address)
        {
            if (_rows.TryGetValue(address, out var current))
            {
                _version++;
                _rows[address] = (current.Entry, $"etag-{_version}");
            }
        }

        public SiloStatus StatusOf(SiloAddress address) => _rows[address].Entry.Status;

        public MembershipEntry EntryOf(SiloAddress address) => _rows[address].Entry;

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
            var result = _rows.TryGetValue(key, out var row)
                ? new MembershipTableData(Tuple.Create(row.Entry, row.ETag), tableVersion)
                : new MembershipTableData(tableVersion);

            OnAfterRead?.Invoke(key);
            return Task.FromResult(result);
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
            OnBeforeUpdate?.Invoke(entry.SiloAddress);
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

    private sealed class RecordingFatalErrorHandler : IFatalErrorHandler
    {
        public int Calls { get; private set; }

        public bool IsUnexpected(Exception exception) => true;

        public void OnFatalException(object? sender = null, string? context = null, Exception? exception = null) => Calls++;
    }
}
