using DigitalBrain.Apps;

[assembly: AssemblyFixture(typeof(DigitalBrain.Mcp.Tests.Integration.McpHost))]

namespace DigitalBrain.Mcp.Tests.Integration;

// The shared host composes the apps module, the superset the MCP surface facts call into; every fact here shares it.
public sealed class McpHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<AppsModule>();
}
