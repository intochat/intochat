using IntoChat.OS.Modules.Apps;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Mcp.Tests.Integration.McpHost))]

namespace IntoChat.OS.Mcp.Tests.Integration;

// The shared host composes the apps module, the superset the MCP surface facts call into; every fact here shares it.
public sealed class McpHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<AppsModule>();
}
