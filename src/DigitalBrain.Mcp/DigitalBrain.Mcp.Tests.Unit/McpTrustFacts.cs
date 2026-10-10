using DigitalBrain.Framework;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Mcp.Tests.Unit;

// R70: the MCP host is an Orleans client, not a silo; it still answers brain access through the trust clients,
// over the private gateway client when the deployment names one.
public sealed class McpTrustFacts
{
    [Theory]
    [InlineData("Open")]
    [InlineData("Secured")]
    public async Task TheMcpHostResolvesIBrainAccess(string posture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = posture;
        builder.Configuration[$"{DigitalBrain.Platform.Trust.TrustOptions.Key}:Gateway"] = "127.0.0.1:30100";
        builder.Services.AddDigitalBrainMcp();
        await using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<IBrainAccess>());
        Assert.NotNull(app.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>());
    }
}
