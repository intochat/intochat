using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Identity.Grants;

// SiloAccessCallFilter trusts a call as internal only when this silo process sent it (SiloOrigin, an object of the process),
// so the rule holds for a cluster of one silo. A silo that runs it refuses to become active beside another live silo of its
// cluster: Orleans would otherwise place grains on both and the filter would refuse ordinary internal calls depending on
// placement. The check runs while this silo is still Joining, before Orleans places any grain on it.
public sealed class SingleSiloGuard(
    IClusterMembershipService membership,
    ILocalSiloDetails local,
    IOptions<ClusterOptions> cluster,
    ILogger<SingleSiloGuard> logger) : ILifecycleParticipant<ISiloLifecycle>
{
    // After the silo has written itself Joining into the membership table, before it becomes Active.
    public const int Stage = ServiceLifecycleStage.BecomeActive - 1;

    private CancellationTokenSource? _watch;

    // The one registration of the silo's access rule: the filter and the guard that keeps its single-silo assumption true.
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
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
        if (others.Length > 0)
        {
            throw new InvalidOperationException(Refusal(cluster.Value.ClusterId, others));
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
            + string.Join(", ", others.Select(member => $"{member.Name} {member.SiloAddress} ({member.Status})"))
            + ". Give each OS host its own Orleans cluster id and membership, or stop the other silo first.";
}
