using DigitalBrain.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Runtime.Orleans.Azure.Tests.Integration;

// A cloud silo serves no Orleans cluster clients unless a gateway port is configured: its process shape publishes the silo and
// HTTP ports only, and a built host binds no gateway. Nothing here touches the network.
public sealed class CloudProfileGatewayFacts
{
    private const string Unreachable = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;QueueEndpoint=http://127.0.0.1:1/devstoreaccount1;TableEndpoint=http://127.0.0.1:1/devstoreaccount1;";

    [Fact]
    public void The_os_process_publishes_no_gateway_port()
    {
        var os = Assert.Single(new CloudProfile().Services.Processes, p => p.Name == "os");

        Assert.Contains(TopologyContext.DefaultSiloPort, os.Ports);
        Assert.DoesNotContain(30000, os.Ports);
    }

    [Fact]
    public void A_built_cloud_host_configures_no_gateway()
    {
        using var home = new TempHome();
        using var host = CloudHosts.Build(Unreachable, home.Path, new FakeClusterStore(), "gateway-facts", typeof(DriveModule));

        Assert.Equal(TopologyContext.NoGateway, host.Services.GetRequiredService<IOptions<EndpointOptions>>().Value.GatewayPort);
    }
}
