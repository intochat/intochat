using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Mcp;

public sealed class McpModule() : Module(new ModuleId("mcp"))
{
    internal const string ManifestName = "mcp";

    // R4-04: what the module needs from its host (McpNeurons).
    public static DigitalBrain.ModuleManifest Manifest => McpNeurons.Manifest;

    // McpServerNeuron is a plain neuron (06-06d) on the runtime's host grains; the kernel lists it for the tool capability
    // source through its instance directory.
    public override Type? Neurons => typeof(McpNeurons);

    public override System.Reflection.Assembly? Contracts => typeof(IMcpServer).Assembly;

    // The neuron opens its own client per call; the module needs no silo services.
}

// The DigitalBrain module of Mcp (06-06d): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module(McpModule.ManifestName)]
public sealed class McpNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
