using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Mcp;

// McpToolConnection is the single connection neuron per MCP server ({brain}/connection/mcp/{name}), providing tool@1.
[DigitalBrain.Modules.Module(ManifestName)]
public sealed class McpModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "mcp";

    public static ConnectorDefinition Connector => new(ManifestName, "MCP Servers", ["bearer-token"], [])
    {
        Neurons = [typeof(IMcpToolConnection)],
    };

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Tools.V1],
        Requires: [],
        Needs: [DigitalBrain.Modules.Need.HttpClient("mcp", requestsPerMinute: 120)],
        Settings: [DigitalBrain.Modules.Setting.Of<McpModuleOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: [])
    {
        Actions = [new("mcp.tool.run", "run an MCP tool", null)],
    };

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IMcpToolConnection).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<McpServersBootstrap>();
        services.AddHostedService(static sp => sp.GetRequiredService<McpServersBootstrap>());
    }
}
