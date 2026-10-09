using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace DigitalBrain.Launcher.Proxy;

// One listener for 5080 and one for 5081; the host header decides which cluster a request
// reaches, and both clusters point at the current revision's block.
public sealed class RevisionProxy(InMemoryConfigProvider config, int uiPort, int mcpPort)
{
    public (int UiPort, int McpPort)? Target { get; private set; }

    public void PointAt(int revisionUiPort, int revisionMcpPort)
    {
        Target = (revisionUiPort, revisionMcpPort);
        config.Update(Routes(uiPort, mcpPort), Clusters(revisionUiPort, revisionMcpPort));
    }

    internal static RouteConfig[] Routes(int uiPort, int mcpPort) =>
    [
        new() { RouteId = "ui", ClusterId = "ui", Match = new() { Path = "/{**catch-all}", Hosts = [$"localhost:{uiPort}"] } },
        new() { RouteId = "mcp", ClusterId = "mcp", Match = new() { Path = "/{**catch-all}", Hosts = [$"localhost:{mcpPort}"] } },
    ];

    internal static ClusterConfig[] Clusters(int revisionUiPort, int revisionMcpPort) =>
    [
        new() { ClusterId = "ui", Destinations = new Dictionary<string, DestinationConfig> { ["current"] = new() { Address = $"http://localhost:{revisionUiPort}/" } } },
        new() { ClusterId = "mcp", Destinations = new Dictionary<string, DestinationConfig> { ["current"] = new() { Address = $"http://localhost:{revisionMcpPort}/" } } },
    ];
}

public static class RevisionProxyServices
{
    public static IServiceCollection AddRevisionProxy(this IServiceCollection services, int uiPort, int mcpPort)
    {
        // Empty until the first PointAt: a request before any revision is current gets YARP's 503.
        services.AddReverseProxy().LoadFromMemory(RevisionProxy.Routes(uiPort, mcpPort), []);
        services.AddSingleton(provider => new RevisionProxy(provider.GetRequiredService<InMemoryConfigProvider>(), uiPort, mcpPort));
        return services;
    }
}
