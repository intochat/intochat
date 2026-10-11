using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Tests.Unit;
using DigitalBrain.Platform.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

namespace DigitalBrain.Mcp.Tests.Unit;

// R93: the MCP process is a machine of the deployment that holds no part. It has its own identity in its own
// directory, registers with the trust host for the owner's account and reads trust over its own link.
public sealed class McpMachineFacts
{
    [Fact]
    public async Task TheMcpRegistersAsAMachineAndReadsTrustOverItsLink()
    {
        if (!System.Net.Quic.QuicConnection.IsSupported) { Assert.Skip("QUIC is not supported on this machine."); }
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        // The owner's machine provisions the owner (R59); the MCP only binds itself to the account.
        await using var owner = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        var directory = Path.Combine(Path.GetTempPath(), $"mcp-{Guid.NewGuid():N}");
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = nameof(IdentityPosture.Open);
        builder.Configuration[$"{TrustOptions.Key}:{nameof(TrustOptions.Endpoint)}"] = trust.Endpoint;
        builder.Configuration[$"{PartsOptions.Key}:{nameof(PartsOptions.MachineDirectory)}"] = directory;
        builder.Configuration[$"{PartsOptions.Key}:{nameof(PartsOptions.PairingSecret)}"] = trust.PairingSecret;
        // The host runs as Production, where a machine names its trust host (T20-3).
        builder.Configuration[$"{TrustOptions.Key}:{nameof(TrustOptions.Pin)}"] = trust.TrustId;
        builder.Services.AddSerializer();
        builder.Services.AddDigitalBrainMcp();
        await using var app = builder.Build();

        foreach (var hosted in app.Services.GetServices<IHostedService>().Where(service => service.GetType().Name == "ClientMachineRegistration"))
        { await hosted.StartAsync(ct); }

        var mcp = app.Services.GetRequiredService<IMachineTransport>().LocalMachineId;
        Assert.True(File.Exists(Path.Combine(directory, "identity.key")));
        Assert.NotEqual(owner.Services.GetRequiredService<IMachineTransport>().LocalMachineId, mcp);
        // An unregistered link is refused every read (T20-4): this read over the MCP's own link proves it registered.
        var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;
        var membership = await app.Services.GetRequiredService<IIdentityClient>().Membership(scope, AccountSession.DefaultLogin);
        Assert.Equal(MemberRole.Owner, membership?.Role);
        Assert.Equal("LinkTrustClients", app.Services.GetRequiredService<IIdentityClient>().GetType().Name);
    }
}
