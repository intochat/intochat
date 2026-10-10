using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class TrustClusterHostingFacts
{
    // R66: the dev trust silo advertises loopback, and the machines' gateway is localhost on the same port.
    [Fact]
    public async Task TheTrustSiloAdvertisesLoopbackAndMachinesReachItOnLocalhost()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, 30100);

        var values = await Environment(builder, trust.Resource);

        Assert.Equal(TrustClusterHosting.Loopback, values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.AdvertisedAddressKey)]);
        Assert.Equal("30100", values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.GatewayPortKey)]);
        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "GrainState"), values.Keys);
        Assert.Equal("localhost:30100", TrustClusterHosting.GatewayOf(30100));
    }

    internal static async Task<Dictionary<string, object>> Environment(IDistributedApplicationBuilder builder, IResource resource)
    {
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
        return values;
    }
}
