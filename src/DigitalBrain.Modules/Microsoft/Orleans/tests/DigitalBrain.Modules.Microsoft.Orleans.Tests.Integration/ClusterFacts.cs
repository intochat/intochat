using DigitalBrain.Microsoft.Orleans;

namespace DigitalBrain.Modules.Microsoft.Orleans.Tests.Integration;

public sealed class ClusterFacts(OrleansHost host)
{
    [Fact]
    public async Task AnInProcessBrainReportsOneActiveSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var owner = await host.PersonAsync(ct: ct);
        var snapshot = await brain.Get<ICluster>(ICluster.Key(owner.BrainId)).Read();
        Assert.Equal("Active", Assert.Single(snapshot.Silos).Status);
    }
}
