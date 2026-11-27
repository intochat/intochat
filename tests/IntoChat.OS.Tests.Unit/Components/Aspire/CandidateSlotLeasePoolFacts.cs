using System.Collections.Concurrent;
using IntoChat.Aspire;
using IntoChat.Aspire.Candidates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

public sealed class CandidateSlotLeasePoolFacts : IDisposable
{
    private readonly string _workdir;

    public CandidateSlotLeasePoolFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-devslot-pool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workdir))
        {
            try
            {
                Directory.Delete(_workdir, recursive: true);
            }
            catch
            {
                // Best-effort temp test directory cleanup.
            }
        }
    }

    [Fact]
    public async Task ConcurrentSlotLeasing_NeverCollidesOnSlotIdsOrPorts_AndNeverDeadlocks()
    {
        var options = new CandidateSlotPoolOptions
        {
            MaxSlots = 6,
            BasePort = 5400,
            PortStride = 10,
            EnableWarmSlotReuse = false,
        };
        await using var pool = new CandidateSlotLeasePool(options, isPortInUse: _ => false);

        var activeSlotIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var activePorts = new ConcurrentDictionary<int, byte>();
        var observedHomes = new ConcurrentBag<string>();
        var collisions = 0;

        var workers = Enumerable.Range(0, 32).Select(async index =>
        {
            var sha = $"{index:x7}";
            await using var lease = await pool.AcquireAsync(
                sha,
                _workdir,
                acquireTimeout: TimeSpan.FromSeconds(10),
                ct: Ct);

            if (!activeSlotIds.TryAdd(lease.SlotId, 0))
            {
                Interlocked.Increment(ref collisions);
            }

            foreach (var port in lease.Ports.AllPorts())
            {
                if (port is 5080 or 5081 or 5190 || !activePorts.TryAdd(port, 0))
                {
                    Interlocked.Increment(ref collisions);
                }
            }

            observedHomes.Add(lease.EphemeralHome);
            Assert.True(Directory.Exists(lease.EphemeralHome));
            await File.WriteAllTextAsync(Path.Combine(lease.EphemeralHome, "marker.txt"), sha, Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(5), Ct);

            activeSlotIds.TryRemove(lease.SlotId, out _);
            foreach (var port in lease.Ports.AllPorts())
            {
                activePorts.TryRemove(port, out _);
            }
        }).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(0, collisions);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(6, pool.AvailableCount);
        Assert.All(observedHomes, home => Assert.False(Directory.Exists(home)));
    }

    [Fact]
    public async Task DynamicPortAllocation_SkipsReservedPorts5080_5081_5190_AndListeningPorts()
    {
        var busyPorts = new HashSet<int> { 5202, 5210, 5225 };
        var options = new CandidateSlotPoolOptions
        {
            MaxSlots = 3,
            BasePort = 5190,
            PortStride = 10,
            EnableWarmSlotReuse = false,
        };
        await using var pool = new CandidateSlotLeasePool(options, isPortInUse: port => busyPorts.Contains(port));

        await using var lease1 = await pool.AcquireAsync("1111111", _workdir, ct: Ct);
        await using var lease2 = await pool.AcquireAsync("2222222", _workdir, ct: Ct);
        await using var lease3 = await pool.AcquireAsync("3333333", _workdir, ct: Ct);

        foreach (var lease in new[] { lease1, lease2, lease3 })
        {
            Assert.False(lease.Ports.Contains(5080));
            Assert.False(lease.Ports.Contains(5081));
            Assert.False(lease.Ports.Contains(5190));
            Assert.All(busyPorts, busy => Assert.False(lease.Ports.Contains(busy)));
        }

        // All three leased ranges must be strictly disjoint.
        var allLeasedPorts = lease1.Ports.AllPorts()
            .Concat(lease2.Ports.AllPorts())
            .Concat(lease3.Ports.AllPorts())
            .ToArray();
        Assert.Equal(allLeasedPorts.Length, allLeasedPorts.Distinct().Count());
    }

    [Fact]
    public async Task EphemeralHome_IsScrubbed_RejectsLiveHome_AndDeletesReadOnlyFilesOnRelease()
    {
        await using var pool = new CandidateSlotLeasePool(
            new CandidateSlotPoolOptions { MaxSlots = 2, BasePort = 5500 },
            isPortInUse: _ => false);

        var lease = await pool.AcquireAsync("a1b2c3d", _workdir, ct: Ct);
        var home = lease.EphemeralHome;

        Assert.True(Directory.Exists(home));
        Assert.True(Directory.Exists(Path.Combine(home, ".config")));
        Assert.True(Directory.Exists(Path.Combine(home, ".local", "share")));
        Assert.True(Directory.Exists(Path.Combine(home, ".cache")));
        Assert.True(Directory.Exists(Path.Combine(home, "tmp")));
        Assert.True(Directory.Exists(Path.Combine(home, "intochat-home")));

        // Zero host dotfiles leaked into ephemeral home.
        Assert.False(Directory.Exists(Path.Combine(home, ".ssh")));
        Assert.False(File.Exists(Path.Combine(home, ".gitconfig")));
        Assert.False(Directory.Exists(Path.Combine(home, ".aws")));
        Assert.False(Directory.Exists(Path.Combine(home, ".azure")));

        // Create a read-only file inside ephemeral home to verify secure deterministic cleanup.
        var readOnlyFile = Path.Combine(home, "intochat-home", "ephemeral-secret.key");
        await File.WriteAllTextAsync(readOnlyFile, "ephemeral-data", Ct);
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        await lease.DisposeAsync();

        Assert.False(Directory.Exists(home));
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(2, pool.AvailableCount);

        // Must refuse a workdir pointing inside the developer's live home.
        if (ProcessCandidateHost.LiveHome() is { } liveHome)
        {
            var ex = await Assert.ThrowsAsync<CandidateHostException>(
                () => pool.AcquireAsync("b2c3d4e", Path.Combine(liveHome, "forbidden"), ct: Ct).AsTask());
            Assert.Equal("slot", ex.Stage);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(2, pool.AvailableCount);
        }
    }

    [Fact]
    public async Task LeaseRelease_IsStrictlyIdempotentUnderConcurrentDisposals()
    {
        await using var pool = new CandidateSlotLeasePool(
            new CandidateSlotPoolOptions { MaxSlots = 1, BasePort = 5550 },
            isPortInUse: _ => false);

        var lease = await pool.AcquireAsync("c3d4e5f", _workdir, ct: Ct);
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(1, pool.ActiveCount);

        var releaseTasks = Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? lease.DisposeAsync().AsTask()
            : pool.ReleaseAsync("c3d4e5f", ct: Ct).AsTask()).ToArray();
        await Task.WhenAll(releaseTasks);
        lease.Dispose();

        Assert.True(lease.IsReleased);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public async Task ExpiredLeases_AreDeterministicallyReclaimed_AndUnblockWaitingAcquirers()
    {
        var clock = new ControllableTimeProvider(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
        var reclaimedShas = new ConcurrentBag<string>();
        await using var pool = new CandidateSlotLeasePool(
            new CandidateSlotPoolOptions
            {
                MaxSlots = 2,
                BasePort = 5600,
                DefaultLeaseTimeout = TimeSpan.FromMinutes(2),
            },
            timeProvider: clock,
            isPortInUse: _ => false,
            onLeaseReclaimed: (expired, _) =>
            {
                reclaimedShas.Add(expired.OwnerSha);
                return ValueTask.CompletedTask;
            });

        var lease1 = await pool.AcquireAsync("111aaaa", _workdir, leaseTimeout: TimeSpan.FromSeconds(30), ct: Ct);
        var lease2 = await pool.AcquireAsync("222bbbb", _workdir, leaseTimeout: TimeSpan.FromMinutes(5), ct: Ct);
        var home1 = lease1.EphemeralHome;
        Assert.True(Directory.Exists(home1));

        // Pool is full before expiry -> acquire times out cleanly without leaking.
        await Assert.ThrowsAsync<TimeoutException>(
            () => pool.AcquireAsync("333cccc", _workdir, acquireTimeout: TimeSpan.FromMilliseconds(25), ct: Ct).AsTask());

        // Advance past lease1's 30s timeout (lease2 still valid).
        clock.Advance(TimeSpan.FromSeconds(31));

        // Next AcquireAsync triggers deterministic reclamation of lease1 and succeeds immediately.
        await using var lease3 = await pool.AcquireAsync("333cccc", _workdir, acquireTimeout: TimeSpan.FromSeconds(2), ct: Ct);

        Assert.True(lease1.IsReleased);
        Assert.False(lease2.IsReleased);
        Assert.False(Directory.Exists(home1));
        Assert.Contains("111aaaa", reclaimedShas);
        Assert.Equal(lease1.SlotId, lease3.SlotId);
        Assert.Equal(2, pool.ActiveCount);

        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task WarmSlotReuse_PrioritizesWarmSlot_PreservesSidecarState_AndIsolatesNewLeaseHome()
    {
        await using var pool = new CandidateSlotLeasePool(
            new CandidateSlotPoolOptions
            {
                MaxSlots = 3,
                BasePort = 5700,
                EnableWarmSlotReuse = true,
            },
            isPortInUse: _ => false);

        var lease1 = await pool.AcquireAsync("aaaa111", _workdir, ct: Ct);
        var firstHome = lease1.EphemeralHome;
        var firstSlotId = lease1.SlotId;
        var firstPorts = lease1.Ports;
        var warmState = new CandidateSlotWarmState(
            "devslot-slot-00",
            "devslot-slot-00-azurite",
            "devslot-slot-00-postgres",
            "devslot-slot-00-clickhouse",
            "pg-secret",
            "ch-secret",
            ReuseCount: 0);
        lease1.AttachWarmState(warmState);

        await lease1.ReleaseAsync(preserveWarmDependencies: true, Ct);

        Assert.False(Directory.Exists(firstHome));
        var snapshotAfterRelease = pool.Snapshot();
        Assert.Equal(1, snapshotAfterRelease.WarmCount);
        Assert.Equal(3, snapshotAfterRelease.AvailableCount);

        // Next acquisition must prioritize the warm slot over the 2 remaining cold slots.
        await using var lease2 = await pool.AcquireAsync("bbbb222", _workdir, ct: Ct);
        Assert.True(lease2.IsWarm);
        Assert.Equal(firstSlotId, lease2.SlotId);
        Assert.Equal(firstPorts, lease2.Ports);
        Assert.NotEqual(lease1.LeaseId, lease2.LeaseId);
        Assert.NotEqual(lease1.LeaseToken, lease2.LeaseToken);
        Assert.NotEqual(firstHome, lease2.EphemeralHome);
        Assert.True(Directory.Exists(lease2.EphemeralHome));

        // Releasing without preserving warm dependencies clears warm state and resets to cold.
        await lease2.ReleaseAsync(preserveWarmDependencies: false, Ct);
        Assert.Equal(0, pool.Snapshot().WarmCount);

        await using var lease3 = await pool.AcquireAsync("cccc333", _workdir, ct: Ct);
        Assert.False(lease3.IsWarm);
    }

    [Fact]
    public async Task DuplicateOwnerSha_IsRejectedWithoutLeakingCapacity()
    {
        await using var pool = new CandidateSlotLeasePool(
            new CandidateSlotPoolOptions { MaxSlots = 2, BasePort = 5800 },
            isPortInUse: _ => false);

        await using var lease = await pool.AcquireAsync("deadbee", _workdir, ct: Ct);
        Assert.Equal(1, pool.ActiveCount);
        Assert.Equal(1, pool.AvailableCount);

        var ex = await Assert.ThrowsAsync<CandidateHostException>(
            () => pool.AcquireAsync("deadbee", _workdir, ct: Ct).AsTask());
        Assert.Equal("slot", ex.Stage);
        Assert.Equal(1, pool.ActiveCount);
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public void AspireOptions_DefaultsToAgentDevSlots_AndValidatesSlotPoolAndUnprivilegedUidConstraints()
    {
        var defaults = new AspireOptions();
        Assert.Equal(CandidateHostMode.AgentDevSlots, defaults.CandidateHostMode);
        defaults.Validate();

        // Root UID 0 and GID 0 must always be refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { CandidateUserId = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { CandidateGroupId = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { MaxCandidateSlots = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { CandidateSlotPortStride = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { CandidateSlotBasePort = 65500, MaxCandidateSlots = 10, CandidateSlotPortStride = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AspireOptions { CandidateSlotLeaseTimeout = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void AspireModule_DefaultsToAgentDevSlotsHostAndSlotPool_WhenModeIsUnset()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        AspireModule.ConfigureServices(services, config);

        var hostRegistration = Assert.Single(services, d => d.ServiceType == typeof(ICandidateHost));
        Assert.Equal(typeof(AgentDevSlotsCandidateHost), hostRegistration.ImplementationType);

        var poolRegistration = Assert.Single(services, d => d.ServiceType == typeof(ICandidateSlotPool));
        Assert.NotNull(poolRegistration.ImplementationFactory);
    }

    [Fact]
    public void AspireModule_RegistersAgentDevSlotsHostAndSlotPool_WhenModeIsAgentDevSlots()
    {
        var section = DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("aspire");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{section}:{nameof(AspireOptions.CandidateHostMode)}"] = nameof(CandidateHostMode.AgentDevSlots),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Options.Create(new AspireOptions { CandidateHostMode = CandidateHostMode.AgentDevSlots }));
        AspireModule.ConfigureServices(services, config);

        var hostRegistration = Assert.Single(services, d => d.ServiceType == typeof(ICandidateHost));
        Assert.Equal(typeof(AgentDevSlotsCandidateHost), hostRegistration.ImplementationType);

        var poolRegistration = Assert.Single(services, d => d.ServiceType == typeof(ICandidateSlotPool));
        Assert.NotNull(poolRegistration.ImplementationFactory);
    }

    private sealed class ControllableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
