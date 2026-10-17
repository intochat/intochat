using DigitalBrain.Microsoft.Orleans;

namespace DigitalBrain.Modules.Microsoft.Orleans.Tests;

public sealed class ClusterFacts
{
    [Fact]
    public async Task AnInProcessBrainReportsOneActiveSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var owner = brain.Owner;
        var snapshot = await brain.Get<ICluster>(ICluster.Key(owner.BrainId)).Read();
        Assert.Equal("Active", Assert.Single(snapshot.Silos).Status);
    }
}
