using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Mcp.Tests.Integration;

// R70: the MCP host is an Orleans client, not a silo; it still answers brain access through the trust clients it
// composes as a reader.
public sealed class McpTrustFacts(McpHost host)
{
    [Theory]
    [InlineData("Open")]
    [InlineData("Secured")]
    public async Task TheMcpHostResolvesIBrainAccess(string posture)
    {
        var runtime = host.Brain;
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(runtime.Services.GetRequiredService<Orleans.IGrainFactory>());
        builder.Configuration[AuthOptions.PostureKey] = posture;
        builder.Services.AddIntoChatOSMcp();
        await using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<IBrainAccess>());
        Assert.NotNull(app.Services.GetRequiredService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>());
    }
}
