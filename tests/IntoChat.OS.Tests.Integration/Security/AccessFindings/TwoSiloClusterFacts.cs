using System.Net;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Two real OS silos in one cluster (loopback, ephemeral ports, this process only): the second host joins the first one's
// membership the way the Azure install's second container app (the Cloud app, or a new revision of the OS app while the old
// one still runs) joins the shared cluster.
[Trait("Category", "AccessFindings")]
public sealed class TwoSiloClusterFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // How long the first silo may take to record the refused second silo as dead once it is told to. Generous: a loaded CI
    // runner is slow, and nothing here measures how fast membership converges.
    private static readonly TimeSpan MembershipBudget = TimeSpan.FromSeconds(60);

    private static Task<Gate20EdgeApprovalWallFacts.EdgeKernelHarness> StartPrimaryAsync()
        => Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);

    // The second silo failed its start after it had written its membership row; the harness's best-effort stop of a failed
    // host does not mark that row dead. Until the first silo's probes find it gone, the first silo's own stop waits a full
    // response timeout on a call to it (call cancellation goes to every silo not dead). Declare it dead and wait until the
    // first silo's membership shows that, so the first silo stops at once with its default timeouts.
    private static async Task ForgetTheRefusedSilo(Gate20EdgeApprovalWallFacts.EdgeKernelHarness a)
    {
        var membership = a.Services.GetRequiredService<IClusterMembershipService>();
        var self = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(MembershipBudget);
        while (membership.CurrentSnapshot.Members.Values.Where(member => !member.SiloAddress.Equals(self) && member.Status != SiloStatus.Dead).ToArray() is { Length: > 0 } others)
        {
            foreach (var other in others) { await membership.TryKill(other.SiloAddress); }
            await Task.Delay(50, limit.Token);
        }
    }

    // Why SingleSiloGuard exists, with the guard taken out of the second silo. The first silo's startup activates the
    // identity grains; the second silo's own startup (a module caller, the integration seeder) calls them on the first
    // silo, which refuses the call because the origin mark of the second process does not count there.
    [Fact]
    public async Task WithoutTheGuard_ASecondSilo_CannotStart_BecauseItsInternalCallsToTheFirstAreRefused()
    {
        await using var a = await StartPrimaryAsync();
        var primary = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.Endpoint;

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var b = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            {
                Join(builder, primary);
                foreach (var guard in builder.Services.Where(descriptor => descriptor.ImplementationType == typeof(SingleSiloGuard)).ToArray())
                {
                    builder.Services.Remove(guard);
                }
            });
        });
        await ForgetTheRefusedSilo(a);

        Assert.Contains(Chain(error), e => e is UnauthorizedAccessException && e.Message.Contains("cannot claim actor kind", StringComparison.Ordinal));
    }

    // The guard: the second silo refuses to become active, with the reason, before Orleans places a grain on it or any
    // startup task runs; it marks its own Joining row Dead before throwing so it does not strand a Joining entry, and the
    // first silo keeps serving alone.
    [Fact]
    public async Task ASecondSilo_RefusesToJoin_AndTheFirstKeepsServing()
    {
        await using var a = await StartPrimaryAsync();
        var primary = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.Endpoint;

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var b = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            {
                Join(builder, primary);
                builder.Services.Configure<SingleSiloGuardOptions>(options => options.GracePeriod = TimeSpan.Zero);
            });
        });

        var refusal = Assert.Single(Chain(error), e => e is InvalidOperationException && e.Message.Contains("must be the only silo of its cluster", StringComparison.Ordinal));
        Assert.Contains(primary.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Chain(error), e => e is UnauthorizedAccessException);

        // Self-cleanup on refusal: refreshing the primary silo's membership snapshot immediately observes the refused
        // second silo as Dead (no stranded Joining row requiring TryKill or MaxJoinAttemptTime expiry).
        var aMembership = a.Services.GetRequiredService<IClusterMembershipService>();
        var aSelf = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        await aMembership.Refresh(default, Ct);
        var peerEntries = aMembership.CurrentSnapshot.Members.Values.Where(member => !member.SiloAddress.Equals(aSelf)).ToArray();
        Assert.NotEmpty(peerEntries);
        Assert.All(peerEntries, peer => Assert.Equal(SiloStatus.Dead, peer.Status));

        var management = a.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0);
        Assert.Single(await management.GetHosts(true, Ct));
        var edge = a.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();
        using (edge.EnterKernel(new BrainId(a.Scope)))
        {
            Assert.Empty(await SiloNeurons.Get<IFriends>(a.Services, FriendsDefaults.Id(a.Scope)).List());
        }
    }

    // Rollout handover: a second silo that starts while the prior silo is still shutting down waits in SingleSiloGuard
    // until the prior silo transitions through ShuttingDown/Stopping to Dead, then becomes active and serves cleanly.
    [Fact]
    public async Task AJoiningSilo_WaitsWithinGracePeriod_AndStartsCleanlyOncePriorSiloBecomesDead()
    {
        var sharedTable = new SharedInMemoryMembershipTable();
        var a = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder => UseSharedMembership(builder, sharedTable));
        var aAddress = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        Task<Gate20EdgeApprovalWallFacts.EdgeKernelHarness>? bStart = null;
        try
        {
            var peerJoining = sharedTable.WaitForNewSiloAsync(excluding: aAddress, Ct);
            bStart = Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            {
                UseSharedMembership(builder, sharedTable);
                builder.Services.Configure<SingleSiloGuardOptions>(options =>
                {
                    options.GracePeriod = TimeSpan.FromSeconds(30);
                    options.PollInterval = TimeSpan.FromMilliseconds(50);
                });
            });

            // Wait until silo B has written its Joining row and reached SingleSiloGuard while silo A is still Active.
            var bAddress = await peerJoining;
            Assert.False(bStart.IsCompleted, "Silo B must wait in SingleSiloGuard while silo A is still non-Dead.");
            Assert.Equal(SiloStatus.Active, sharedTable.StatusOf(aAddress));
            Assert.Equal(SiloStatus.Joining, sharedTable.StatusOf(bAddress));

            // Gracefully stop silo A (Active -> ShuttingDown -> Stopping -> Dead).
            await a.DisposeAsync();
            a = null;

            await using var b = await bStart;
            bStart = null;

            Assert.Equal(SiloStatus.Dead, sharedTable.StatusOf(aAddress));
            Assert.Equal(SiloStatus.Active, sharedTable.StatusOf(bAddress));
            var edge = b.Services.GetRequiredService<EdgeAuthority>();
            RequestContext.Clear();
            using (edge.EnterKernel(new BrainId(b.Scope)))
            {
                Assert.Empty(await SiloNeurons.Get<IFriends>(b.Services, FriendsDefaults.Id(b.Scope)).List());
            }
        }
        finally
        {
            if (a is not null) { await a.DisposeAsync(); }
            if (bStart is not null)
            {
                try { await (await bStart).DisposeAsync(); }
                catch { }
            }
        }
    }

    // Self-cleanup end-to-end: after silo B is refused while silo A runs, B marks its own row Dead (leaving A untouched).
    // Once A stops cleanly, silo C starts immediately with zero grace period — proving B never stranded a Joining row.
    [Fact]
    public async Task ARefusedSilo_MarksItselfDead_SoASubsequentSiloStartsCleanlyAfterPrimaryStops()
    {
        var sharedTable = new SharedInMemoryMembershipTable();
        var a = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder => UseSharedMembership(builder, sharedTable));
        var aAddress = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var b = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
                {
                    UseSharedMembership(builder, sharedTable);
                    builder.Services.Configure<SingleSiloGuardOptions>(options => options.GracePeriod = TimeSpan.Zero);
                });
            });

            Assert.Contains(Chain(error), e => e is InvalidOperationException && e.Message.Contains("must be the only silo of its cluster", StringComparison.Ordinal));
            Assert.Equal(SiloStatus.Active, sharedTable.StatusOf(aAddress));
            var refusedRows = sharedTable.Statuses().Where(pair => !pair.Key.Equals(aAddress)).ToArray();
            Assert.Single(refusedRows);
            Assert.Equal(SiloStatus.Dead, refusedRows[0].Value);
        }
        finally
        {
            await a.DisposeAsync();
        }

        Assert.Equal(SiloStatus.Dead, sharedTable.StatusOf(aAddress));

        // No ForgetTheRefusedSilo, no 5-minute MaxJoinAttemptTime wait: silo C starts immediately with GracePeriod = 0.
        await using var c = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
        {
            UseSharedMembership(builder, sharedTable);
            builder.Services.Configure<SingleSiloGuardOptions>(options => options.GracePeriod = TimeSpan.Zero);
        });
        var cAddress = c.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        Assert.Equal(SiloStatus.Active, sharedTable.StatusOf(cAddress));
    }

    // Ungraceful kill recovery end-to-end: when a prior silo crashed or was SIGKILLed without writing Dead, its stale
    // membership row (non-advancing IAmAliveTime older than the staleness threshold AND unreachable TCP endpoint) is
    // marked Dead by SingleSiloGuard before BecomeActive so a new revision boots cleanly instead of being bricked.
    [Fact]
    public async Task AJoiningSilo_ReapsStaleZombiePeerRow_AndStartsCleanly()
    {
        var sharedTable = new SharedInMemoryMembershipTable();
        var closedPort = ReserveClosedLoopbackPort();
        var zombieAddress = SiloAddress.New(IPAddress.Loopback, closedPort, 1);
        sharedTable.SeedRow(
            zombieAddress,
            SiloStatus.Active,
            startTime: DateTime.UtcNow.AddMinutes(-10),
            iAmAliveTime: DateTime.UtcNow.AddMinutes(-5));

        await using var fresh = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
        {
            UseSharedMembership(builder, sharedTable);
            builder.Services.Configure<SingleSiloGuardOptions>(options => options.GracePeriod = TimeSpan.Zero);
        });

        var freshAddress = fresh.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        Assert.Equal(SiloStatus.Dead, sharedTable.StatusOf(zombieAddress));
        Assert.Equal(SiloStatus.Active, sharedTable.StatusOf(freshAddress));

        var edge = fresh.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();
        using (edge.EnterKernel(new BrainId(fresh.Scope)))
        {
            Assert.Empty(await SiloNeurons.Get<IFriends>(fresh.Services, FriendsDefaults.Id(fresh.Scope)).List());
        }
    }

    // Fail-closed TCP probe guard end-to-end: when a real live primary silo is partitioned from updating IAmAliveTime
    // (stale and non-advancing across IAmAliveTablePublishTimeout), SingleSiloGuard refuses to mark it Dead because the
    // bounded TCP probe to peer.SiloAddress.Endpoint succeeds, leaving the live primary serving uninterrupted.
    [Fact]
    public async Task AJoiningSilo_RefusesToReapStalePeerWithReachableTcpSocket()
    {
        var sharedTable = new SharedInMemoryMembershipTable();
        await using var primary = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder => UseSharedMembership(builder, sharedTable));
        var primaryAddress = primary.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        // Simulate a membership DB write partition on the still-live primary: its IAmAliveTime is 5m old and frozen.
        sharedTable.FreezeHeartbeat(primaryAddress, DateTime.UtcNow.AddMinutes(-5));

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var joining = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            {
                UseSharedMembership(builder, sharedTable);
                builder.Services.Configure<SingleSiloGuardOptions>(options => options.GracePeriod = TimeSpan.Zero);
                builder.Services.Configure<Orleans.Configuration.ClusterMembershipOptions>(options =>
                {
                    options.IAmAliveTablePublishTimeout = TimeSpan.FromMilliseconds(50);
                });
            });
        });

        Assert.Contains(Chain(error), e => e is InvalidOperationException && e.Message.Contains("must be the only silo of its cluster", StringComparison.Ordinal));
        Assert.Equal(SiloStatus.Active, sharedTable.StatusOf(primaryAddress));
        var otherRows = sharedTable.Statuses().Where(pair => !pair.Key.Equals(primaryAddress)).ToArray();
        Assert.Single(otherRows);
        Assert.Equal(SiloStatus.Dead, otherRows[0].Value);

        var edge = primary.Services.GetRequiredService<EdgeAuthority>();
        RequestContext.Clear();
        using (edge.EnterKernel(new BrainId(primary.Scope)))
        {
            Assert.Empty(await SiloNeurons.Get<IFriends>(primary.Services, FriendsDefaults.Id(primary.Scope)).List());
        }
    }

    private static int ReserveClosedLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // A silo alone in its cluster starts with the guard in place: the single-silo shape is unchanged.
    [Fact]
    public async Task ASiloAlone_StartsWithTheGuard()
    {
        await using var a = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        Assert.Contains(a.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(), participant => participant is SingleSiloGuard);
    }

    private static void UseSharedMembership(WebApplicationBuilder builder, SharedInMemoryMembershipTable table)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IMembershipTable>(table));
        // The harness disposes a host whose start failed without stopping it; its silo connection still receives membership
        // gossip and would log into a disposed Windows event log, which ends the test process.
        builder.Logging.ClearProviders();
    }

    internal static void Join(WebApplicationBuilder builder, IPEndPoint primary)
    {
        var siloPort = int.Parse(builder.Configuration[DigitalBrain.Runtime.Orleans.TopologyContext.SiloPortKey]!, System.Globalization.CultureInfo.InvariantCulture);
        builder.UseOrleans(orleans => orleans.UseLocalhostClustering(siloPort, DigitalBrain.Runtime.Orleans.TopologyContext.NoGateway, primary));
        // The harness disposes a host whose start failed without stopping it; its silo connection still receives the first
        // silo's membership gossip and would log into a disposed Windows event log, which ends the test process.
        builder.Logging.ClearProviders();
    }

    internal static IEnumerable<Exception> Chain(Exception error)
    {
        var pending = new Stack<Exception>([error]);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate) { foreach (var inner in aggregate.InnerExceptions) { pending.Push(inner); } }
            else if (current.InnerException is { } inner) { pending.Push(inner); }
        }
    }

    // Externalized in-process Orleans IMembershipTable shared by real loopback silos so stopping silo A does not tear
    // down the membership table itself (matching Azure Table / Postgres persistence in production).
    private sealed class SharedInMemoryMembershipTable : IMembershipTable
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<SiloAddress, (MembershipEntry Entry, string ETag)> _rows = [];
        private readonly HashSet<SiloAddress> _frozenHeartbeats = [];
        private readonly List<(SiloAddress Excluded, TaskCompletionSource<SiloAddress> Waiter)> _waiters = [];
        private int _version = 1;

        public void FreezeHeartbeat(SiloAddress address, DateTime staleTimestamp)
        {
            lock (_gate)
            {
                _frozenHeartbeats.Add(address);
                if (_rows.TryGetValue(address, out var current))
                {
                    var updated = Clone(current.Entry);
                    updated.IAmAliveTime = staleTimestamp;
                    updated.StartTime = staleTimestamp;
                    _rows[address] = (updated, current.ETag);
                }
            }
        }

        public void SeedRow(SiloAddress address, SiloStatus status, DateTime startTime, DateTime iAmAliveTime)
        {
            lock (_gate)
            {
                _version++;
                _rows[address] = (
                    new MembershipEntry
                    {
                        SiloAddress = address,
                        SiloName = $"zombie-{address.Endpoint.Port}",
                        HostName = "localhost",
                        Status = status,
                        StartTime = startTime,
                        IAmAliveTime = iAmAliveTime,
                        SuspectTimes = [],
                    },
                    _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public Task<SiloAddress> WaitForNewSiloAsync(SiloAddress excluding, CancellationToken ct)
        {
            lock (_gate)
            {
                foreach (var address in _rows.Keys)
                {
                    if (!address.Equals(excluding))
                    {
                        return Task.FromResult(address);
                    }
                }

                var waiter = new TaskCompletionSource<SiloAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => waiter.TrySetCanceled(ct));
                _waiters.Add((excluding, waiter));
                return waiter.Task;
            }
        }

        public SiloStatus StatusOf(SiloAddress address)
        {
            lock (_gate)
            {
                return _rows[address].Entry.Status;
            }
        }

        public KeyValuePair<SiloAddress, SiloStatus>[] Statuses()
        {
            lock (_gate)
            {
                return [.. _rows.Select(pair => new KeyValuePair<SiloAddress, SiloStatus>(pair.Key, pair.Value.Entry.Status))];
            }
        }

        public Task InitializeMembershipTable(bool tryInitTableVersion) => Task.CompletedTask;

        public Task DeleteMembershipTableEntries(string clusterId)
        {
            lock (_gate)
            {
                _rows.Clear();
                _version++;
            }

            return Task.CompletedTask;
        }

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => Task.CompletedTask;

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            lock (_gate)
            {
                var tv = new TableVersion(_version, _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (_rows.TryGetValue(key, out var row))
                {
                    return Task.FromResult(new MembershipTableData(Tuple.Create(Clone(row.Entry), row.ETag), tv));
                }

                return Task.FromResult(new MembershipTableData(tv));
            }
        }

        public Task<MembershipTableData> ReadAll()
        {
            lock (_gate)
            {
                var tv = new TableVersion(_version, _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var members = _rows.Values.Select(row => Tuple.Create(Clone(row.Entry), row.ETag)).ToList();
                return Task.FromResult(new MembershipTableData(members, tv));
            }
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            lock (_gate)
            {
                if (_rows.ContainsKey(entry.SiloAddress) || tableVersion.Version != _version + 1)
                {
                    return Task.FromResult(false);
                }

                _version = tableVersion.Version;
                _rows[entry.SiloAddress] = (Clone(entry), _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                NotifyWaitersLocked(entry.SiloAddress);
                return Task.FromResult(true);
            }
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue(entry.SiloAddress, out var current)
                    || !string.Equals(current.ETag, etag, StringComparison.Ordinal)
                    || tableVersion.Version != _version + 1)
                {
                    return Task.FromResult(false);
                }

                _version = tableVersion.Version;
                _rows[entry.SiloAddress] = (Clone(entry), _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(true);
            }
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            lock (_gate)
            {
                if (!_frozenHeartbeats.Contains(entry.SiloAddress) && _rows.TryGetValue(entry.SiloAddress, out var current))
                {
                    var updated = Clone(current.Entry);
                    updated.IAmAliveTime = entry.IAmAliveTime;
                    _rows[entry.SiloAddress] = (updated, current.ETag);
                }
            }

            return Task.CompletedTask;
        }

        private void NotifyWaitersLocked(SiloAddress inserted)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Excluded.Equals(inserted))
                {
                    _waiters[i].Waiter.TrySetResult(inserted);
                    _waiters.RemoveAt(i);
                }
            }
        }

        private static MembershipEntry Clone(MembershipEntry source) => new()
        {
            SiloAddress = source.SiloAddress,
            Status = source.Status,
            SuspectTimes = source.SuspectTimes is { } suspects ? [.. suspects] : [],
            ProxyPort = source.ProxyPort,
            HostName = source.HostName,
            SiloName = source.SiloName,
            RoleName = source.RoleName,
            UpdateZone = source.UpdateZone,
            FaultZone = source.FaultZone,
            StartTime = source.StartTime,
            IAmAliveTime = source.IAmAliveTime,
        };
    }
}
