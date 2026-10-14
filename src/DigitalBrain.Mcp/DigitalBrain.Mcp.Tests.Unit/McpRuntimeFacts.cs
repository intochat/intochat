using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Mcp.Tests.Unit;

public sealed class McpRuntimeFacts
{
    [Fact]
    public async Task TheMcpReadsMembershipThroughTheSingleRuntimeWithoutMachineRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var services = new ServiceCollection();
        services.AddSingleton(brain.Services.GetRequiredService<Orleans.IGrainFactory>());
        services.AddLocalPlatformClients();
        await using var client = services.BuildServiceProvider();
        var membership = await client.GetRequiredService<IIdentityClient>().Membership("owner", "owner");
        Assert.Equal(MemberRole.Owner, membership?.Role);
    }
}
