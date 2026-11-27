using DigitalBrain;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.Logging;

namespace IntoChat.Aspire.Candidates;

/// <summary>
/// Tier-aware Plane 2 <see cref="ICandidateHost"/> (INT-2599 Slice 2) that prefers an eligible online
/// Paired PC advertising <c>AllowConnectedEngineering == true</c> (Tier 1, $0 Azure compute) via
/// <see cref="IConnectedCandidateRelay"/> and cleanly falls back to local <see cref="AgentDevSlotsCandidateHost"/>
/// (Tier 2) or Cloud ACA Job (Tier 3) whenever no eligible Paired PC is online.
/// </summary>
internal sealed class ConnectedDeviceCandidateHost : ICandidateHost
{
    private static readonly ILogger Log = OsConsoleLog.For<ConnectedDeviceCandidateHost>();

    private readonly ICandidateHost _fallbackHost;
    private readonly IConnectedCandidateRelay? _connectedRelay;
    private readonly BrainId _defaultBrainId;

    public ConnectedDeviceCandidateHost(
        AgentDevSlotsCandidateHost fallbackHost,
        IConnectedCandidateRelay? connectedRelay = null)
        : this(fallbackHost, connectedRelay, new BrainId(IntoChatOSNames.DefaultOwnerBrainId))
    {
    }

    internal ConnectedDeviceCandidateHost(
        ICandidateHost fallbackHost,
        IConnectedCandidateRelay? connectedRelay = null,
        BrainId? defaultBrainId = null)
    {
        _fallbackHost = fallbackHost ?? throw new ArgumentNullException(nameof(fallbackHost));
        _connectedRelay = connectedRelay;
        _defaultBrainId = defaultBrainId ?? new BrainId(IntoChatOSNames.DefaultOwnerBrainId);
    }

    public string FallbackHostKind => _fallbackHost.GetType().Name;

    public int Tier1HitCount => Volatile.Read(ref _tier1HitCount);

    public int FallbackCount => Volatile.Read(ref _fallbackCount);

    public ConnectedCandidateTarget? LastSelectedDevice { get; private set; }

    private int _tier1HitCount;
    private int _fallbackCount;

    /// <summary>
    /// Probes whether an eligible online Paired PC advertising <c>AllowConnectedEngineering == true</c>
    /// is available for <paramref name="brainId"/> (Tier 1, $0 Azure compute).
    /// </summary>
    public async Task<ConnectedCandidateTarget?> TryResolveTier1DeviceAsync(
        BrainId? brainId = null,
        CancellationToken ct = default)
    {
        if (_connectedRelay is null)
        {
            return null;
        }

        var target = await _connectedRelay
            .TrySelectEligibleDeviceAsync(brainId ?? _defaultBrainId, ct)
            .ConfigureAwait(false);
        LastSelectedDevice = target;
        if (target is not null)
        {
            Interlocked.Increment(ref _tier1HitCount);
        }
        else
        {
            Interlocked.Increment(ref _fallbackCount);
        }

        return target;
    }

    public async Task<PreparedCandidate> Prepare(
        string baseSha,
        string summary,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
    {
        await LogTierResolutionAsync("prepare", ct).ConfigureAwait(false);
        return await _fallbackHost.Prepare(baseSha, summary, files, ct).ConfigureAwait(false);
    }

    public async Task BuildGate(string sha, CancellationToken ct)
    {
        await LogTierResolutionAsync("build-gate", ct).ConfigureAwait(false);
        await _fallbackHost.BuildGate(sha, ct).ConfigureAwait(false);
    }

    public async Task<string> Publish(string sha, CancellationToken ct)
    {
        await LogTierResolutionAsync("publish", ct).ConfigureAwait(false);
        return await _fallbackHost.Publish(sha, ct).ConfigureAwait(false);
    }

    public async Task<RunningCandidate> Run(string sha, CancellationToken ct)
    {
        await LogTierResolutionAsync("run", ct).ConfigureAwait(false);
        return await _fallbackHost.Run(sha, ct).ConfigureAwait(false);
    }

    public Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
        => _fallbackHost.WaitHealthy(sha, budget, ct);

    public Task<string?> Commit(string sha, CancellationToken ct)
        => _fallbackHost.Commit(sha, ct);

    public Task Uncommit(string sha, string? previous, CancellationToken ct)
        => _fallbackHost.Uncommit(sha, previous, ct);

    public Task Remove(string sha, CancellationToken ct)
        => _fallbackHost.Remove(sha, ct);

    public Task<string[]> Orphans(string[] known, CancellationToken ct)
        => _fallbackHost.Orphans(known, ct);

    private async Task LogTierResolutionAsync(string stage, CancellationToken ct)
    {
        var target = await TryResolveTier1DeviceAsync(ct: ct).ConfigureAwait(false);
        if (target is not null)
        {
            Log.LogDebug(
                "ConnectedDeviceCandidateHost observed Tier 1 Paired PC '{Device}' ({MachineId}) during '{Stage}'; delegating local candidate lifecycle to {Fallback}.",
                target.DeviceName,
                target.MachineId,
                stage,
                FallbackHostKind);
        }
    }
}
