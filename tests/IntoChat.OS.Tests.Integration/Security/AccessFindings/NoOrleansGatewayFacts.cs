using System.Net;
using System.Net.Sockets;
using DigitalBrain.Runtime.Orleans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// The full OS composition on the local topology, as the Desktop and a local brain start it: no gateway port is configured, so
// the silo opens no gateway listener, and the host still serves its edge and its own grain calls.
[Trait("Category", "AccessFindings")]
public sealed class NoOrleansGatewayFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALocalOsHost_OpensNoGateway_AndServes()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);

        Assert.Equal(TopologyContext.NoGateway, host.Services.GetRequiredService<IOptions<EndpointOptions>>().Value.GatewayPort);
        Assert.Null(host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress);
        Assert.False(await Listens(host.GatewayPort), "Nothing listens on the port the harness reserved for a gateway.");
        using var ready = await host.Client.GetAsync("/ready", Ct);
        Assert.True(ready.IsSuccessStatusCode);
    }

    [Fact]
    public async Task AHostThatOptsIn_OpensItsGateway()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, openGateway: true);

        Assert.Equal(host.GatewayPort, host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress?.Endpoint.Port);
        Assert.True(await Listens(host.GatewayPort), "The opted-in gateway listens.");
    }

    // A loopback connect to the port this harness reserved itself; nothing else on the machine is probed.
    private static async Task<bool> Listens(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
            return true;
        }
        catch (SocketException) { return false; }
    }
}
