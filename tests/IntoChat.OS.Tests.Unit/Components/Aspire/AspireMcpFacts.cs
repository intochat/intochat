using IntoChat.Aspire;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

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

    // The sample above has no display name: the view carries none and the rule in DisplayNameOf decides.
    [Fact]
    public void WithoutADisplayNameFieldTheViewHasNone()
        => Assert.All(AspireMcp.Resources(ListResources), view => Assert.Null(view.DisplayName));

    [Theory]
    [InlineData("displayName")]
    [InlineData("display_name")]
    [InlineData("DisplayName")]
    public void ADisplayNameFieldIsRead(string field)
    {
        var text = "# RESOURCE DATA\r\n\r\n[ { \"name\": \"os-gdnnbbha\", \"" + field + "\": \"os\", \"state\": \"Running\", \"urls\": [] } ]";
        var view = Assert.Single(AspireMcp.Resources(text));
        Assert.Equal(("os-gdnnbbha", "os"), (view.Name, view.DisplayName));
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
