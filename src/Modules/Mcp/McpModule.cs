using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Mcp;

// McpServerNeuron is a plain neuron on the runtime's host grains. Configured servers reconcile at host start.
[DigitalBrain.Modules.Module(ManifestName)]
public sealed class McpModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "mcp";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [DigitalBrain.Modules.Need.HttpClient("mcp", requestsPerMinute: 120)],
        Settings: [DigitalBrain.Modules.Setting.Of<McpModuleOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: [])
    {
        Actions = [new("mcp.tool.run", "run an MCP tool", null)],
    };

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IMcpServer).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<McpServersBootstrap>();
        services.AddHostedService(static sp => sp.GetRequiredService<McpServersBootstrap>());
    }
}
