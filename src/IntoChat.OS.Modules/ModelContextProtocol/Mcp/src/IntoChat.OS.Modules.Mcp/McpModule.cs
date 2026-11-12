using DigitalBrain.Modules;
namespace IntoChat.OS.Modules.Mcp;

// McpServerNeuron is a plain neuron on the runtime's host grains. It opens its own client per call; the module needs no silo services.
[DigitalBrain.Modules.Module(ManifestName)]
public sealed class McpModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "mcp";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [DigitalBrain.Modules.Need.HttpClient("mcp", requestsPerMinute: 120)],
        Settings: [],
        Secrets: [])
    {
        Actions = [new("mcp.tool.run", "run an MCP tool", null)],
    };

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IMcpServer).Assembly;
}
