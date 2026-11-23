using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Azure.Tests.Unit;

// Ino reads the running app through Aspire's MCP server: only reading tools, only under a local AppHost, and list_resources becomes
// the contract's resource views.
public sealed class AspireMcpFacts
{
    private const string ListResources = "resource_name is the identifier of resources.\r\n\r\n# PAGINATION\r\n\r\n{ \"offset\": 0 }\r\n\r\n# RESOURCE DATA\r\n\r\n"
        + "[ { \"name\": \"os\", \"state\": \"Running\", \"urls\": [ { \"name\": \"http\", \"url\": \"http://localhost:14963\" } ] },"
        + " { \"name\": \"intochat\", \"state\": \"Running\", \"urls\": [] } ]";

    [Fact]
    public void ListResourcesBecomesResourceViews()
    {
        var views = AspireMcp.Resources(ListResources);
        Assert.Equal(["os", "intochat"], views.Select(view => view.Name));
        Assert.Equal("http://localhost:14963", views[0].Endpoint);
        Assert.Equal("", views[1].Endpoint);
    }

    [Fact]
    public async Task ChangingToolsAreRefused()
    {
        await using var mcp = new AspireMcp(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AspireMcp.AppHostKey] = @"C:\app\AppHost.csproj" }).Build());
        await Assert.ThrowsAsync<ArgumentException>(() => mcp.Call("execute_resource_command", new Dictionary<string, object?>(), CancellationToken.None));
    }

    [Fact]
    public async Task WithoutALocalAppHostItIsNotAvailable()
    {
        await using var mcp = new AspireMcp(new ConfigurationBuilder().Build());
        Assert.False(mcp.Available);
    }
}
