# IntoChat.OS.Modules.Mcp

Generic MCP client: any Streamable-HTTP MCP server as a tool-source neuron.

Module id: `mcp`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/ModelContextProtocol/Mcp/tests/IntoChat.OS.Modules.Mcp.Tests.Integration/IntoChat.OS.Modules.Mcp.Tests.Integration.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:mcp:Options` (environment prefix `IntoChatOS__Modules__mcp__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Servers` | `Dictionary<String, McpServerOptions>` | (collection) | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Mcp` (`IBrainToolsConnection`, `IMcpServer`, `IMcpToolConnection`, `IToolSource`).
Hosting: runs in process.
<!-- END GENERATED -->
