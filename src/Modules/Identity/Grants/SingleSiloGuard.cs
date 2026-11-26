using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Identity.Grants;

public sealed class SingleSiloGuardOptions
{
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

// SiloAccessCallFilter trusts a call as internal only when this silo process sent it (SiloOrigin, an object of the process),
// so the rule holds for a cluster of one silo. A silo that runs it refuses to become active beside another live silo of its
// cluster: Orleans would otherwise place grains on both and the filter would refuse ordinary internal calls depending on
// placement. The check runs while this silo is still Joining, before Orleans places any grain on it.
public sealed class SingleSiloGuard(
    IClusterMembershipService membership,
    ILocalSiloDetails local,
    IOptions<ClusterOptions> cluster,
    ILogger<SingleSiloGuard> logger,
    IMembershipTable? membershipTable = null,
    IOptions<SingleSiloGuardOptions>? options = null,
    TimeProvider? timeProvider = null) : ILifecycleParticipant<ISiloLifecycle>
{
    // After the silo has written itself Joining into the membership table, before it becomes Active.
    public const int Stage = ServiceLifecycleStage.BecomeActive - 1;

    private CancellationTokenSource? _watch;

    // The one registration of the silo's access rule: the filter and the guard that keeps its single-silo assumption true.
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SingleSiloGuardOptions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, SiloAccessCallFilter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<ISiloLifecycle>, SingleSiloGuard>());
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe(nameof(SingleSiloGuard), Stage, CheckAsync);
        lifecycle.Subscribe(nameof(SingleSiloGuard), ServiceLifecycleStage.Active, StartWatching, StopWatching);
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await membership.Refresh(default, ct).ConfigureAwait(false);
        var others = Others(membership.CurrentSnapshot, local.SiloAddress);
        if (others.Length == 0)
        {
            return;
        }

        var guardOptions = options?.Value ?? new SingleSiloGuardOptions();
        var gracePeriod = guardOptions.GracePeriod > TimeSpan.Zero ? guardOptions.GracePeriod : TimeSpan.Zero;
        var pollInterval = guardOptions.PollInterval > TimeSpan.Zero ? guardOptions.PollInterval : TimeSpan.FromSeconds(1);

        if (gracePeriod > TimeSpan.Zero)
        {
            var clock = timeProvider ?? TimeProvider.System;
            var started = clock.GetTimestamp();
            logger.LogWarning(
                "Cluster '{ClusterId}' still has live peer silo(s) ({Peers}) while {LocalSilo} is joining; waiting up to {GracePeriod} for prior silo shutdown to complete.",
                cluster.Value.ClusterId,
                FormatMembers(others),
                local.SiloAddress,
                gracePeriod);

            while (others.Length > 0)
            {
                var remaining = gracePeriod - clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delay = remaining < pollInterval ? remaining : pollInterval;
                await Task.Delay(delay, clock, ct).ConfigureAwait(false);
                await membership.Refresh(default, ct).ConfigureAwait(false);
                others = Others(membership.CurrentSnapshot, local.SiloAddress);
            }

            if (others.Length == 0)
            {
                logger.LogInformation(
                    "Prior silo(s) left cluster '{ClusterId}' after {Elapsed}; {LocalSilo} is clear to become active.",
                    cluster.Value.ClusterId,
                    clock.GetElapsedTime(started),
                    local.SiloAddress);
                return;
            }
        }

        await MarkSelfDeadAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException(Refusal(cluster.Value.ClusterId, others));
    }

    // Best-effort cleanup of this silo's own Joining row right before refusing startup. Without this, Orleans leaves
    // the refused silo as Joining until MaxJoinAttemptTime expires, which blocks subsequent boots or stalls live peers.
    // Never throws: the single-silo refusal must always win.
    private async Task MarkSelfDeadAsync(CancellationToken ct)
    {
        if (membershipTable is null)
        {
            return;
        }

        var clock = timeProvider ?? TimeProvider.System;
        using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cleanupCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var data = await membershipTable.ReadRowAsync(local.SiloAddress, cleanupCts.Token).ConfigureAwait(false);
                if (data.TryGet(local.SiloAddress) is not { } row || row.Item1.Status == SiloStatus.Dead)
                {
                    return;
                }

                var now = clock.GetUtcNow().UtcDateTime;
                var source = row.Item1;
                var updated = new MembershipEntry
                {
                    SiloAddress = source.SiloAddress,
                    Status = SiloStatus.Dead,
                    SuspectTimes = source.SuspectTimes is { } suspects ? [.. suspects] : [],
                    ProxyPort = source.ProxyPort,
                    HostName = source.HostName,
                    SiloName = source.SiloName,
                    RoleName = source.RoleName,
                    UpdateZone = source.UpdateZone,
                    FaultZone = source.FaultZone,
                    StartTime = source.StartTime,
                    IAmAliveTime = now,
                };
                updated.AddSuspector(local.SiloAddress, now);

                if (await membershipTable.UpdateRowAsync(updated, row.Item2, data.Version.Next(), cleanupCts.Token).ConfigureAwait(false))
                {
                    return;
                }
            }

            logger.LogWarning("Unable to mark refused silo {SiloAddress} as Dead in membership table after 3 attempts due to write contention.", local.SiloAddress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Failed to mark refused silo {SiloAddress} as Dead in membership table before refusing startup.", local.SiloAddress);
        }
    }

    // A silo that joined anyway (a build without this guard, or a race this check lost) is reported, never silently served.
    private Task StartWatching(CancellationToken ct)
    {
        _watch = new CancellationTokenSource();
        var stop = _watch.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var reported = string.Empty;
                await foreach (var snapshot in membership.MembershipUpdates.WithCancellation(stop).ConfigureAwait(false))
                {
                    var others = Others(snapshot, local.SiloAddress, onlyActive: true);
                    var refusal = others.Length > 0 ? Refusal(cluster.Value.ClusterId, others) : string.Empty;
                    if (refusal.Length > 0 && !string.Equals(refusal, reported, StringComparison.Ordinal)) { logger.LogCritical("{Refusal}", refusal); }
                    reported = refusal;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task StopWatching(CancellationToken ct)
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        return Task.CompletedTask;
    }

    // Live members other than this silo. A silo shutting down or stopping is leaving and holds no new placement.
    internal static ClusterMember[] Others(ClusterMembershipSnapshot snapshot, SiloAddress self, bool onlyActive = false)
        => [.. snapshot.Members.Values.Where(member => !member.SiloAddress.Equals(self)
            && (onlyActive ? member.Status == SiloStatus.Active : member.Status is SiloStatus.Created or SiloStatus.Joining or SiloStatus.Active))];

    internal static string Refusal(string clusterId, IEnumerable<ClusterMember> others)
        => $"This silo runs the IntoChat OS access rule (SiloAccessCallFilter), which treats a call as internal only when this silo process sent it, "
            + $"so it must be the only silo of its cluster. Cluster '{clusterId}' already has: "
            + FormatMembers(others)
            + ". Give each OS host its own Orleans cluster id and membership, or stop the other silo first.";

    private static string FormatMembers(IEnumerable<ClusterMember> members)
        => string.Join(", ", members.Select(member => $"{member.Name} {member.SiloAddress} ({member.Status})"));
}
