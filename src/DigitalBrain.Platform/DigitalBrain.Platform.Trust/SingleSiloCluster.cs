using Orleans.Configuration;

namespace DigitalBrain.Platform;

// R105: the trust host and every machine are single-silo clusters, so a membership row left Active by the silo's own
// previous run (killed, or on other ports in an --isolated run) must never stop the next start. Orleans 10.3.1 has no
// switch for the startup connectivity check (ValidateInitialConnectivity is a lifecycle stage, not an option); the
// check skips any silo whose IAmAlive is older than IAmAliveTablePublishTimeout × NumMissedTableIAmAliveLimit, so the
// heartbeat is published often enough that a dead run's row is skipped within that window. Defunct-silo cleanup then
// removes the row.
public static class SingleSiloCluster
{
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    public const int MissedHeartbeats = 2;

    public static ISiloBuilder AsSingleSiloCluster(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        return silo.Configure<ClusterMembershipOptions>(membership =>
        {
            membership.IAmAliveTablePublishTimeout = HeartbeatEvery;
            membership.NumMissedTableIAmAliveLimit = MissedHeartbeats;
        });
    }
}
