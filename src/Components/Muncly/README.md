# IntoChat.Muncly

Muncly CRM component: embedded full-bleed WebBrowser app (`app.muncly`) with stored web credentials and authenticated MCP server integration (`muncly-crm`) for agents.

Module id: `muncly`.

## Configuration

- `IntoChatOS:Modules:muncly:Options:BaseUrl` — Muncly CRM base URL (e.g. `https://crm.munc.ly`).
- `IntoChatOS:Modules:muncly:Options:ToolSets` — optional comma-separated Muncly tool sets (e.g. `core,setup` or `all`).
- `IntoChatOS:Modules:muncly:Options:AllowedTools` — optional MCP tool allow-list.
- `IntoChatOS:Integrations:muncly:McpToken` — Muncly MCP Bearer token (secret; stored in user-secrets / Vault, never committed).
- `IntoChatOS:Integrations:muncly:WebEmail` / `WebPassword` — optional web credentials for the embedded browser session.

When both `BaseUrl` and `McpToken` are configured, the kernel automatically registers the `muncly-crm` server in `McpModule` pointing at `{BaseUrl}/api/mcp` with `Authorization: Bearer <token>`.
