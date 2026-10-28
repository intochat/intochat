using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Mcp.Tests.Integration;

public sealed class McpRuntimeFacts(McpHost host)
{
    [Fact]
    public async Task TheMcpReadsMembershipThroughTheSingleRuntimeWithoutMachineRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var services = new ServiceCollection();
        global::IntoChat.OS.Kernel.Composition.KernelTestHooks.ShareGrains(services, brain.Services);
        services.AddLocalPlatformClients();
        await using var client = services.BuildServiceProvider();
        var membership = await client.GetRequiredService<IIdentityClient>().Membership("owner", "owner");
        Assert.Equal(MemberRole.Owner, membership?.Role);
    }
}
