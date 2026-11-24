# IntoChat.Treg

treg MCP connection preset and pure `SKILL.md` agent-app importer.

## Boundary

`IntoChat.Treg` holds all treg-specific metadata outside `IntoChat.OS.Modules.Mcp` so the MCP module never names any external registry or service.

- `TregPreset`: produces `McpConnectionConfig` presets targeting the verified treg MCP endpoints (`https://treg.to/mcp/` and curated `https://treg.to/mcp/v2/`) using `Authorization: Bearer <token>`, exact verified tool allowlists, and `McpConfirmationMode.OncePerSession` by default (treg bills per call against the user's prepaid balance). No credentials are stored in the preset; the person seals their bearer token into the brain's `IMcpToolConnection` vault when connecting.
- `SkillImport`: pure, deterministic parser that converts a `SKILL.md` file into a validated Rung 1 agent `AppManifest` with one required `treg` (`tool@1`) capability slot.

## Verified Upstream Details

- **License (`superdesigndev/treg`):** Apache 2.0 with Additional Terms restricting hosted/embedded redistribution of treg server code. Connecting to hosted `treg.to` as an MCP client with the user's own token is explicitly permitted.
- **Billing:** Prepaid pay-per-call at 0% markup; `catalog_search` and `catalog_get` are free and report per-call USD prices before execution; failed upstream calls are unbilled.
- **Endpoints & Tools:**
  - Standard (`https://treg.to/mcp/`): `catalog_search`, `catalog_get`, `call`, `call_result`, `balance` (plus team tools and imported skills).
  - Curated v2 (`https://treg.to/mcp/v2/`): `catalog_search`, `catalog_get`, `catalog_call_read`, `catalog_call_write`, `balance`.
