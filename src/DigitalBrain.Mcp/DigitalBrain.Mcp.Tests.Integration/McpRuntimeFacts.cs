using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Authority.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Mcp.Tests.Integration;

public sealed class McpRuntimeFacts(McpHost host)
{
    [Fact]
    public async Task TheMcpReadsMembershipThroughTheSingleRuntimeWithoutMachineRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var services = new ServiceCollection();
        services.AddSingleton(brain.Services.GetRequiredService<Orleans.IGrainFactory>());
        services.AddLocalPlatformClients();
        await using var client = services.BuildServiceProvider();
        var membership = await client.GetRequiredService<IIdentityClient>().Membership("owner", "owner");
        Assert.Equal(MemberRole.Owner, membership?.Role);
    }
}
