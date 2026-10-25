using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Mcp;

public sealed class McpModule() : Module(new ModuleId("mcp"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IMcpServer).Assembly;

    // The neuron opens its own client per call; the module needs no silo services.
    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);
}
