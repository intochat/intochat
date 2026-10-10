using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire.Deployment;
using DigitalBrain.Microsoft.Orleans;

namespace DigitalBrain.Microsoft.Aspire.Tests;

// The orleans module's cluster neuron as the deployment reads it: the membership is whatever the fake host reports.
[global::Orleans.GrainType("orleans.cluster")]
public sealed class FakeMembership(IServiceProvider services) : Neuron, ICluster
{
    private long version;

    public async Task<ClusterSnapshot> Read()
    {
        var host = (FakeClusterHost)services.GetService(typeof(IClusterHost))!;
        return new ClusterSnapshot(await host.Silos(), ++version);
    }
}
