using DigitalBrain.Tools;

[assembly: AssemblyFixture(typeof(DigitalBrain.Mcp.Tests.McpHost))]

namespace DigitalBrain.Mcp.Tests;

// The shared host composes the Mcp and Tools modules; each fact connects its own fake MCP server from a fresh person's
// brain, so the server's URL is the fact's, not the host's.
public sealed class McpHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<McpModule>().WithModule<ToolsModule>();
}
