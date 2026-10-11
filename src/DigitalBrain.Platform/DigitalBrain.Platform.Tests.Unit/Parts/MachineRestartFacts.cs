using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// R105: a machine silo is a single-silo cluster; its previous run's stale Active row is skipped by the startup
// connectivity check once the row's heartbeat is older than the window.
public sealed class MachineRestartFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AMachineSiloPublishesItsHeartbeatOftenEnoughToOutliveAStaleRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machine = await ModuleTest.Create().WithPartLog().StartAsync(ct);

        var membership = machine.SiloServices.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;

        Assert.Equal(SingleSiloCluster.HeartbeatEvery, membership.IAmAliveTablePublishTimeout);
        Assert.Equal(SingleSiloCluster.MissedHeartbeats, membership.NumMissedTableIAmAliveLimit);
    }
}
