namespace IntoChat.Aspire.Candidates;

internal sealed record CandidateSlotPortRange(int BasePort, int Stride)
{
    public int HttpPort => BasePort;
    public int McpPort => BasePort + 1;
    public int AzuriteBlobPort => BasePort + 2;
    public int AzuriteTablePort => BasePort + 3;
    public int PostgresPort => BasePort + 4;
    public int ClickHousePort => BasePort + 5;
    public int EndPortExclusive => BasePort + Stride;

    public bool Contains(int port) => port >= BasePort && port < EndPortExclusive;

    public IEnumerable<int> AllPorts() => Enumerable.Range(BasePort, Stride);
}

internal sealed record CandidateSlotPoolOptions
{
    public const int MinPortStride = 6;
    public const int DefaultMaxSlots = 8;
    public const int DefaultBasePort = 5300;
    public const int DefaultPortStride = 10;
    public const int DefaultCandidateUserId = 1000;
    public const int DefaultCandidateGroupId = 1000;
    public static readonly TimeSpan DefaultLeaseTimeoutValue = TimeSpan.FromMinutes(15);

    public int MaxSlots { get; init; } = DefaultMaxSlots;
    public int BasePort { get; init; } = DefaultBasePort;
    public int PortStride { get; init; } = DefaultPortStride;
    public TimeSpan DefaultLeaseTimeout { get; init; } = DefaultLeaseTimeoutValue;
    public bool EnableWarmSlotReuse { get; init; } = true;
    public int CandidateUserId { get; init; } = DefaultCandidateUserId;
    public int CandidateGroupId { get; init; } = DefaultCandidateGroupId;
    public string EphemeralHomeRootName { get; init; } = "slot-homes";

    public string UserSpec => $"{CandidateUserId}:{CandidateGroupId}";

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSlots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSlots, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(BasePort, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BasePort, 60000);
        ArgumentOutOfRangeException.ThrowIfLessThan(PortStride, MinPortStride);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PortStride, 100);
        if (BasePort + (MaxSlots * PortStride) > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BasePort),
                BasePort,
                $"Port range [{BasePort}, {BasePort + (MaxSlots * PortStride)}) exceeds maximum TCP port 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DefaultLeaseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DefaultLeaseTimeout, TimeSpan.FromHours(24));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CandidateUserId, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CandidateGroupId, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(EphemeralHomeRootName);
    }
}

internal sealed record CandidateSlotWarmState(
    string NetworkName,
    string AzuriteContainer,
    string PostgresContainer,
    string ClickHouseContainer,
    string PostgresPassword,
    string ClickHousePassword,
    int ReuseCount = 0)
{
    public CandidateSlotWarmState NextReuse() => this with { ReuseCount = ReuseCount + 1 };
}

internal sealed record CandidateSlotPoolSnapshot(
    int Capacity,
    int AvailableCount,
    int ActiveCount,
    int WarmCount,
    IReadOnlyList<string> ActiveSlotIds);

internal sealed class CandidateSlotLease : IAsyncDisposable, IDisposable
{
    private readonly Func<CandidateSlotLease, bool, CancellationToken, ValueTask> _releaseAsync;
    private readonly Action<CandidateSlotLease, bool> _releaseSync;
    private int _released;
    private volatile CandidateSlotWarmState? _warmState;

    internal CandidateSlotLease(
        string slotId,
        int slotIndex,
        string leaseId,
        string leaseToken,
        string ownerSha,
        CandidateSlotPortRange ports,
        string ephemeralHome,
        string userSpec,
        DateTimeOffset acquiredAt,
        DateTimeOffset expiresAt,
        CandidateSlotWarmState? warmState,
        Func<CandidateSlotLease, bool, CancellationToken, ValueTask> releaseAsync,
        Action<CandidateSlotLease, bool> releaseSync)
    {
        SlotId = slotId;
        SlotIndex = slotIndex;
        LeaseId = leaseId;
        LeaseToken = leaseToken;
        OwnerSha = ownerSha;
        Ports = ports;
        EphemeralHome = ephemeralHome;
        UserSpec = userSpec;
        AcquiredAt = acquiredAt;
        ExpiresAt = expiresAt;
        _warmState = warmState;
        _releaseAsync = releaseAsync;
        _releaseSync = releaseSync;
    }

    public string SlotId { get; }
    public int SlotIndex { get; }
    public string LeaseId { get; }
    public string LeaseToken { get; }
    public string OwnerSha { get; }
    public CandidateSlotPortRange Ports { get; }
    public string EphemeralHome { get; }
    public string UserSpec { get; }
    public DateTimeOffset AcquiredAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool IsReleased => Volatile.Read(ref _released) != 0;
    public CandidateSlotWarmState? WarmState => _warmState;
    public bool IsWarm => _warmState is not null;

    public string PostgresDatabaseName => $"intochat_os_{LeaseToken}";
    public string ClickHouseDatabaseName => $"candidate_{LeaseToken}";
    public string GrainStateContainerName => $"grainstate-{LeaseToken}";

    public void AttachWarmState(CandidateSlotWarmState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _warmState = state;
    }

    public void ClearWarmState() => _warmState = null;

    internal bool TryMarkReleased() => Interlocked.CompareExchange(ref _released, 1, 0) == 0;

    public ValueTask ReleaseAsync(bool preserveWarmDependencies = false, CancellationToken ct = default)
        => _releaseAsync(this, preserveWarmDependencies, ct);

    public ValueTask DisposeAsync() => ReleaseAsync(preserveWarmDependencies: false, CancellationToken.None);

    public void Dispose() => _releaseSync(this, false);
}

internal interface ICandidateSlotPool : IAsyncDisposable, IDisposable
{
    int Capacity { get; }
    int AvailableCount { get; }
    int ActiveCount { get; }

    ValueTask<CandidateSlotLease> AcquireAsync(
        string ownerSha,
        string workdir,
        TimeSpan? leaseTimeout = null,
        TimeSpan? acquireTimeout = null,
        CancellationToken ct = default);

    bool TryGetActiveLease(string ownerSha, out CandidateSlotLease? lease);

    ValueTask ReleaseAsync(string ownerSha, bool preserveWarmDependencies = false, CancellationToken ct = default);

    ValueTask<int> ReclaimExpiredAsync(CancellationToken ct = default);

    IReadOnlyList<CandidateSlotWarmState> DrainWarmStates();

    CandidateSlotPoolSnapshot Snapshot();
}
