using DigitalBrain.Launcher.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace DigitalBrain.Launcher.Tests.Unit;

public sealed class RevisionProxyFacts
{
    [Fact]
    public void PointingAtARevisionRoutesUiAndMcpHostsToItsPorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRevisionProxy(uiPort: 5080, mcpPort: 5081);
        using var provider = services.BuildServiceProvider();
        var proxy = provider.GetRequiredService<RevisionProxy>();
        Assert.Null(proxy.Target);
        proxy.PointAt(5090, 5091);
        var config = provider.GetRequiredService<IProxyConfigProvider>().GetConfig();
        Assert.Equal((5090, 5091), proxy.Target);
        Assert.Equal("http://localhost:5090/", config.Clusters.Single(c => c.ClusterId == "ui").Destinations!["current"].Address);
        Assert.Equal("http://localhost:5091/", config.Clusters.Single(c => c.ClusterId == "mcp").Destinations!["current"].Address);
        Assert.Equal("5080", config.Routes.Single(r => r.RouteId == "ui").Match.Hosts!.Single().Split(':')[1]);
        Assert.Equal("5081", config.Routes.Single(r => r.RouteId == "mcp").Match.Hosts!.Single().Split(':')[1]);
        proxy.PointAt(5100, 5101);
        Assert.Equal("http://localhost:5100/", provider.GetRequiredService<IProxyConfigProvider>().GetConfig().Clusters.Single(c => c.ClusterId == "ui").Destinations!["current"].Address);
    }
}
