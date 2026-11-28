using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.Aspire.Candidates;

// Lock-free / atomic pool of leased devcontainer slots for Plane 2 (agent-dev-slots).
// Manages isolated slot IDs, dynamic non-overlapping host port ranges (never 5080/5081/5190),
// scrubbed ephemeral $HOME directories, lease timeouts, deterministic reclamation, and warm-slot reuse.
internal sealed class CandidateSlotLeasePool : ICandidateSlotPool
{
    private const int LiveHttpPort = 5080;
    private const int LiveMcpPort = 5081;
    private const int LegacyDockerFirstPort = 5190;
    private const int LegacyDockerBlockSize = 6;
    private const int MaxPortSearchWindows = 512;

    private static readonly ILogger Log = OsConsoleLog.For<CandidateSlotLeasePool>();
    private static readonly string[] IsolatedHomeSubdirectories =
    [
        ".config",
        ".local",
        Path.Combine(".local", "share"),
        ".cache",
        "tmp",
        "intochat-home",
    ];

    // Bounded lock-free reservation bitmap over the closed TCP port space [0..65535] so concurrent
    // real-OS pools in the same process never hand out overlapping host port ranges.
    private static readonly int[] ProcessReservedPorts = new int[ushort.MaxValue + 1];

    private readonly CandidateSlotPoolOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, bool> _isPortInUse;
    private readonly Func<CandidateSlotLease, CancellationToken, ValueTask>? _onLeaseReclaimed;
    private readonly SemaphoreSlim _availableSlots;
    private readonly ConcurrentQueue<SlotEntry> _warmSlots = new();
    private readonly ConcurrentQueue<SlotEntry> _coldSlots = new();
    private readonly ConcurrentDictionary<string, CandidateSlotLease> _activeByOwner = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SlotEntry> _slotsById = new(StringComparer.Ordinal);
    private readonly int[] _reservedPorts;
    private readonly ConcurrentDictionary<int, CandidateSlotPortRange> _ownedRanges = new();
    private int _portCursor;
    private int _disposed;

    private sealed class SlotEntry(string slotId, int slotIndex)
    {
        public string SlotId { get; } = slotId;
        public int SlotIndex { get; } = slotIndex;
        public CandidateSlotPortRange? LastPorts { get; set; }
        public CandidateSlotWarmState? WarmState { get; set; }
    }

    public CandidateSlotLeasePool(
        CandidateSlotPoolOptions? options = null,
        TimeProvider? timeProvider = null,
        Func<int, bool>? isPortInUse = null,
        Func<CandidateSlotLease, CancellationToken, ValueTask>? onLeaseReclaimed = null)
    {
        _options = options ?? new CandidateSlotPoolOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isPortInUse = isPortInUse ?? IsTcpPortInUse;
        _reservedPorts = isPortInUse is null ? ProcessReservedPorts : new int[ushort.MaxValue + 1];
        _onLeaseReclaimed = onLeaseReclaimed;
        _availableSlots = new SemaphoreSlim(_options.MaxSlots, _options.MaxSlots);

        for (var i = 0; i < _options.MaxSlots; i++)
        {
            var slot = new SlotEntry($"slot-{i:D2}", i);
            _slotsById[slot.SlotId] = slot;
            _coldSlots.Enqueue(slot);
        }
    }

    public int Capacity => _options.MaxSlots;
    public int AvailableCount => Volatile.Read(ref _disposed) != 0 ? 0 : _availableSlots.CurrentCount;
    public int ActiveCount => _activeByOwner.Count;

    public async ValueTask<CandidateSlotLease> AcquireAsync(
        string ownerSha,
        string workdir,
        TimeSpan? leaseTimeout = null,
        TimeSpan? acquireTimeout = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(workdir);

        var effectiveLeaseTimeout = leaseTimeout ?? _options.DefaultLeaseTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveLeaseTimeout, TimeSpan.Zero);

        if (_activeByOwner.ContainsKey(ownerSha))
        {
            throw new CandidateHostException("slot", $"Candidate '{ownerSha}' already holds an active dev slot lease.");
        }

        // Deterministically reclaim any expired leases before waiting for capacity.
        if (_availableSlots.CurrentCount == 0)
        {
            await ReclaimExpiredAsync(ct).ConfigureAwait(false);
        }

        var acquiredPermit = await WaitForPermitAsync(acquireTimeout, ct).ConfigureAwait(false);
        if (!acquiredPermit)
        {
            // One final reclamation pass in case a lease expired during the wait window.
            var reclaimed = await ReclaimExpiredAsync(ct).ConfigureAwait(false);
            if (reclaimed == 0 || !await _availableSlots.WaitAsync(0, ct).ConfigureAwait(false))
            {
                throw new TimeoutException(
                    $"Timed out waiting for an available candidate dev slot (capacity={Capacity}, active={ActiveCount}).");
            }
        }

        SlotEntry? slot = null;
        string? ephemeralHome = null;
        CandidateSlotPortRange? allocatedPorts = null;
        var allocatedFreshPorts = false;
        try
        {
            slot = DequeueSlot();
            var warmState = _options.EnableWarmSlotReuse ? slot.WarmState : null;
            if (warmState is not null && slot.LastPorts is { } warmPorts && AreCandidateEntryPortsFree(warmPorts))
            {
                allocatedPorts = warmPorts;
            }
            else
            {
                // If an existing warm slot's ports are no longer usable or warm reuse is disabled, release its port block and reallocate.
                if (slot.LastPorts is { } stalePorts)
                {
                    ReleasePortRange(stalePorts);
                    slot.LastPorts = null;
                }

                warmState = null;
                slot.WarmState = null;
                allocatedPorts = AllocatePortRange(slot.SlotIndex);
                allocatedFreshPorts = true;
            }

            var leaseToken = CreateLeaseToken();
            var leaseId = $"{slot.SlotId}-{leaseToken}";
            ephemeralHome = CreateScrubbedEphemeralHome(workdir, leaseId);

            var now = _timeProvider.GetUtcNow();
            var lease = new CandidateSlotLease(
                slot.SlotId,
                slot.SlotIndex,
                leaseId,
                leaseToken,
                ownerSha,
                allocatedPorts,
                ephemeralHome,
                _options.UserSpec,
                now,
                now.Add(effectiveLeaseTimeout),
                warmState,
                ReleaseLeaseCoreAsync,
                ReleaseLeaseSync);

            if (!_activeByOwner.TryAdd(ownerSha, lease))
            {
                throw new CandidateHostException("slot", $"Candidate '{ownerSha}' already holds an active dev slot lease.");
            }

            slot.LastPorts = allocatedPorts;
            return lease;
        }
        catch
        {
            if (ephemeralHome is not null)
            {
                TryDeleteDirectorySync(ephemeralHome);
            }

            if (allocatedFreshPorts && allocatedPorts is not null)
            {
                ReleasePortRange(allocatedPorts);
            }

            if (slot is not null)
            {
                ReturnSlotToQueue(slot);
            }

            _availableSlots.Release();
            throw;
        }
    }

    public bool TryGetActiveLease(string ownerSha, out CandidateSlotLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSha);
        return _activeByOwner.TryGetValue(ownerSha, out lease);
    }

    public ValueTask ReleaseAsync(string ownerSha, bool preserveWarmDependencies = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSha);
        if (!_activeByOwner.TryGetValue(ownerSha, out var lease))
        {
            return ValueTask.CompletedTask;
        }

        return ReleaseLeaseCoreAsync(lease, preserveWarmDependencies, ct);
    }

    public async ValueTask<int> ReclaimExpiredAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var reclaimed = 0;
        foreach (var lease in _activeByOwner.Values)
        {
            if (lease.IsReleased || lease.ExpiresAt > now)
            {
                continue;
            }

            if (_onLeaseReclaimed is not null)
            {
                try
                {
                    await _onLeaseReclaimed(lease, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.LogWarning(ex, "Reclamation callback failed for expired slot lease {LeaseId} ({Sha}).", lease.LeaseId, lease.OwnerSha);
                }
            }

            if (!lease.IsReleased)
            {
                await ReleaseLeaseCoreAsync(lease, preserveWarmDependencies: false, ct).ConfigureAwait(false);
                reclaimed++;
            }
        }

        return reclaimed;
    }

    public IReadOnlyList<CandidateSlotWarmState> DrainWarmStates()
    {
        var drained = new List<CandidateSlotWarmState>();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return drained;
        }

        // Acquire permits non-blockingly so queue mutations remain strictly synchronized with AcquireAsync.
        var held = new List<SlotEntry>();
        try
        {
            while (held.Count < _options.MaxSlots && _availableSlots.Wait(0))
            {
                if (_warmSlots.TryDequeue(out var warm))
                {
                    if (warm.WarmState is { } state)
                    {
                        drained.Add(state);
                        warm.WarmState = null;
                    }

                    if (warm.LastPorts is { } ports)
                    {
                        ReleasePortRange(ports);
                        warm.LastPorts = null;
                    }

                    held.Add(warm);
                }
                else
                {
                    _availableSlots.Release();
                    break;
                }
            }
        }
        finally
        {
            foreach (var slot in held)
            {
                _coldSlots.Enqueue(slot);
                _availableSlots.Release();
            }
        }

        return drained;
    }

    public CandidateSlotPoolSnapshot Snapshot()
    {
        var activeSlotIds = _activeByOwner.Values
            .Select(lease => lease.SlotId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var warmCount = _slotsById.Values.Count(slot => slot.WarmState is not null);
        return new CandidateSlotPoolSnapshot(
            Capacity,
            AvailableCount,
            activeSlotIds.Length,
            warmCount,
            activeSlotIds);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var lease in _activeByOwner.Values.ToArray())
        {
            if (lease.TryMarkReleased())
            {
                _activeByOwner.TryRemove(lease.OwnerSha, out _);
                await DeleteDirectoryWithRetryAsync(lease.EphemeralHome, CancellationToken.None).ConfigureAwait(false);
            }
        }

        ReleaseAllOwnedPortRanges();
        _availableSlots.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var lease in _activeByOwner.Values.ToArray())
        {
            if (lease.TryMarkReleased())
            {
                _activeByOwner.TryRemove(lease.OwnerSha, out _);
                TryDeleteDirectorySync(lease.EphemeralHome);
            }
        }

        ReleaseAllOwnedPortRanges();
        _availableSlots.Dispose();
    }

    private async ValueTask<bool> WaitForPermitAsync(TimeSpan? acquireTimeout, CancellationToken ct)
    {
        if (acquireTimeout is { } timeout)
        {
            return await _availableSlots.WaitAsync(timeout, ct).ConfigureAwait(false);
        }

        await _availableSlots.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private SlotEntry DequeueSlot()
    {
        if (_options.EnableWarmSlotReuse && _warmSlots.TryDequeue(out var warm))
        {
            return warm;
        }

        if (_coldSlots.TryDequeue(out var cold))
        {
            return cold;
        }

        if (_warmSlots.TryDequeue(out var remainingWarm))
        {
            return remainingWarm;
        }

        throw new InvalidOperationException("Slot pool permit acquired without an available slot entry.");
    }

    private async ValueTask ReleaseLeaseCoreAsync(CandidateSlotLease lease, bool preserveWarmDependencies, CancellationToken ct)
    {
        if (!lease.TryMarkReleased())
        {
            return;
        }

        _activeByOwner.TryRemove(lease.OwnerSha, out _);
        try
        {
            await DeleteDirectoryWithRetryAsync(lease.EphemeralHome, ct).ConfigureAwait(false);
        }
        finally
        {
            CompleteSlotReturn(lease, preserveWarmDependencies);
        }
    }

    private void ReleaseLeaseSync(CandidateSlotLease lease, bool preserveWarmDependencies)
    {
        if (!lease.TryMarkReleased())
        {
            return;
        }

        _activeByOwner.TryRemove(lease.OwnerSha, out _);
        TryDeleteDirectorySync(lease.EphemeralHome);
        CompleteSlotReturn(lease, preserveWarmDependencies);
    }

    private void CompleteSlotReturn(CandidateSlotLease lease, bool preserveWarmDependencies)
    {
        if (!_slotsById.TryGetValue(lease.SlotId, out var slot))
        {
            ReleasePortRange(lease.Ports);
            return;
        }

        var keepWarm = preserveWarmDependencies && _options.EnableWarmSlotReuse && lease.WarmState is not null;
        if (keepWarm)
        {
            slot.WarmState = lease.WarmState;
            slot.LastPorts = lease.Ports;
        }
        else
        {
            slot.WarmState = null;
            slot.LastPorts = null;
            ReleasePortRange(lease.Ports);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ReturnSlotToQueue(slot);
        try
        {
            _availableSlots.Release();
        }
        catch (ObjectDisposedException ex)
        {
            Log.LogDebug(ex, "Slot pool was disposed while returning slot {SlotId}.", lease.SlotId);
        }
    }

    private void ReturnSlotToQueue(SlotEntry slot)
    {
        if (slot.WarmState is not null && _options.EnableWarmSlotReuse)
        {
            _warmSlots.Enqueue(slot);
        }
        else
        {
            _coldSlots.Enqueue(slot);
        }
    }

    private CandidateSlotPortRange AllocatePortRange(int slotIndex)
    {
        var stride = _options.PortStride;
        var baseStart = _options.BasePort;
        var maxWindows = Math.Min(MaxPortSearchWindows, Math.Max(1, (65535 - baseStart) / stride));
        var startCursor = (int)((uint)(Interlocked.Increment(ref _portCursor) + slotIndex - 1) % (uint)maxWindows);

        for (var offset = 0; offset < maxWindows; offset++)
        {
            var windowIndex = (startCursor + offset) % maxWindows;
            var candidateBase = baseStart + (windowIndex * stride);
            if (candidateBase + stride > 65535)
            {
                continue;
            }

            var range = new CandidateSlotPortRange(candidateBase, stride);
            if (OverlapsReservedPorts(range))
            {
                continue;
            }

            if (!TryReservePortRange(range))
            {
                continue;
            }

            if (IsAnyPortInRangeListening(range))
            {
                ReleasePortRange(range);
                continue;
            }

            return range;
        }

        throw new CandidateHostException("slot", "Unable to allocate a collision-free dynamic port range for candidate dev slot.");
    }

    private bool TryReservePortRange(CandidateSlotPortRange range)
    {
        for (var port = range.BasePort; port < range.EndPortExclusive; port++)
        {
            if (Interlocked.CompareExchange(ref _reservedPorts[port], 1, 0) != 0)
            {
                for (var rollback = range.BasePort; rollback < port; rollback++)
                {
                    Volatile.Write(ref _reservedPorts[rollback], 0);
                }

                return false;
            }
        }

        _ownedRanges.TryAdd(range.BasePort, range);
        return true;
    }

    private void ReleasePortRange(CandidateSlotPortRange range)
    {
        if (_ownedRanges.TryRemove(range.BasePort, out _))
        {
            for (var port = range.BasePort; port < range.EndPortExclusive; port++)
            {
                Volatile.Write(ref _reservedPorts[port], 0);
            }
        }
    }

    private void ReleaseAllOwnedPortRanges()
    {
        foreach (var range in _ownedRanges.Values.ToArray())
        {
            ReleasePortRange(range);
        }
    }

    private bool AreCandidateEntryPortsFree(CandidateSlotPortRange range)
        => !OverlapsReservedPorts(range) && !_isPortInUse(range.HttpPort) && !_isPortInUse(range.McpPort);

    private bool IsAnyPortInRangeListening(CandidateSlotPortRange range)
    {
        for (var port = range.BasePort; port < range.EndPortExclusive; port++)
        {
            if (_isPortInUse(port))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool OverlapsReservedPorts(CandidateSlotPortRange range)
    {
        if (range.Contains(LiveHttpPort) || range.Contains(LiveMcpPort))
        {
            return true;
        }

        for (var legacyPort = LegacyDockerFirstPort; legacyPort < LegacyDockerFirstPort + LegacyDockerBlockSize; legacyPort++)
        {
            if (range.Contains(legacyPort))
            {
                return true;
            }
        }

        return false;
    }

    private string CreateScrubbedEphemeralHome(string workdir, string leaseId)
    {
        var root = Path.GetFullPath(workdir);
        var ephemeralHome = Path.GetFullPath(Path.Combine(root, _options.EphemeralHomeRootName, leaseId));
        EnsureIsolatedFromHostHomes(ephemeralHome);

        Directory.CreateDirectory(ephemeralHome);
        foreach (var relativeSubdir in IsolatedHomeSubdirectories)
        {
            Directory.CreateDirectory(Path.Combine(ephemeralHome, relativeSubdir));
        }

        return ephemeralHome;
    }

    internal static void EnsureIsolatedFromHostHomes(string candidateHome)
    {
        var normalized = NormalizePath(candidateHome);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (ProcessCandidateHost.LiveHome() is { } liveHome)
        {
            var normalizedLive = NormalizePath(liveHome);
            if (string.Equals(normalized, normalizedLive, comparison)
                || normalized.StartsWith(normalizedLive + Path.DirectorySeparatorChar, comparison))
            {
                throw new CandidateHostException(
                    "slot",
                    $"Ephemeral slot home '{normalized}' must not equal or reside inside live home '{normalizedLive}'.");
            }
        }

        var userProfile = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var normalizedProfile = NormalizePath(userProfile);
            if (string.Equals(normalized, normalizedProfile, comparison))
            {
                throw new CandidateHostException(
                    "slot",
                    $"Ephemeral slot home '{normalized}' must never equal host user profile '{normalizedProfile}'.");
            }

            foreach (var sensitiveFolder in new[] { ".ssh", ".aws", ".azure", ".kube", ".gnupg" })
            {
                var sensitivePath = NormalizePath(Path.Combine(normalizedProfile, sensitiveFolder));
                if (string.Equals(normalized, sensitivePath, comparison)
                    || normalized.StartsWith(sensitivePath + Path.DirectorySeparatorChar, comparison))
                {
                    throw new CandidateHostException(
                        "slot",
                        $"Ephemeral slot home '{normalized}' must never overlap host sensitive directory '{sensitivePath}'.");
                }
            }
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string CreateLeaseToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static bool IsTcpPortInUse(int port)
    {
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port))
        {
            return true;
        }

        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            if (!OperatingSystem.IsWindows())
            {
                probe.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            probe.Start();
            probe.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Log.LogDebug(ex, "Retrying cleanup of ephemeral slot home '{Path}' (attempt {Attempt}).", path, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning(ex, "Best-effort cleanup of ephemeral slot home '{Path}' failed after retries.", path);
            }
        }
    }

    private static void TryDeleteDirectorySync(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            ClearReadOnlyAttributes(path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Best-effort synchronous cleanup of ephemeral slot home '{Path}' failed.", path);
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogDebug(ex, "Unable to clear attributes on '{File}' during ephemeral home cleanup.", file);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
