using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Mcp;

public sealed class McpModule() : Module(new ModuleId("mcp"))
{
    // R4-04: what the module needs from its host: nothing. McpServerNeuron stays a grain until plain-aware Instances
    // (06-06d), because the tool capability source finds MCP servers through the instance directory.
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    public override System.Reflection.Assembly? Contracts => typeof(IMcpServer).Assembly;

    // The neuron opens its own client per call; the module needs no silo services.
    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);
}
