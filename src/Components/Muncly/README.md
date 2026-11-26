# IntoChat.Muncly

Muncly CRM component: embedded full-bleed WebBrowser app (`app.muncly`) with stored web credentials and authenticated MCP server integration (`muncly-crm`) for agents.

Module id: `muncly`.

## Configuration

- `IntoChatOS:Modules:muncly:Options:BaseUrl` — Muncly CRM base URL (e.g. `https://crm.munc.ly`).
- `IntoChatOS:Modules:muncly:Options:ToolSets` — optional comma-separated Muncly tool sets (e.g. `core,setup` or `all`).
- `IntoChatOS:Modules:muncly:Options:AllowedTools` — optional MCP tool allow-list.
- `IntoChatOS:Integrations:muncly:McpToken` — Muncly MCP Bearer token (secret; stored in user-secrets / Vault, never committed).
- `IntoChatOS:Integrations:muncly:WebEmail` / `WebPassword` — optional web credentials for the embedded browser session.

When both `BaseUrl` and `McpToken` are configured (either in startup configuration or at runtime via **Settings -> Integrations -> Muncly CRM** in the OS Vault), `MunclyApp` opens the full-bleed CRM browser and `MunclyMcpSeedProvider` seeds the owner brain's `muncly-crm` `McpToolConnection` (`{BaseUrl}/api/mcp` with sealed `Authorization: Bearer <token>`). When `BaseUrl` is not yet configured, `MunclyApp` renders an inline setup card.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:muncly:Options` (environment prefix `IntoChatOS__Modules__muncly__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `AllowedTools` | `String[]` | unset | no |
| `BaseUrl` | `String` | unset | no |
| `Enabled` | `Boolean` | `True` | no |
| `ToolSets` | `String` | unset | no |
| `BaseUrl` | integration setting | unset | no |
| `ToolSets` | integration setting | unset | no |
| `WebEmail` | integration setting | unset | no |

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `McpToken` | no |
| `WebPassword` | no |

Contracts: assembly `IntoChat.Muncly`.
Hosting: runs in process.
<!-- END GENERATED -->
