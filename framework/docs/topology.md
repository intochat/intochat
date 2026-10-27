# Topology

A topology is the shape a brain runs in. Select it with `IntoChatOS:Topology` (environment variable
`IntoChatOS__Topology`).

| Topology | Package | Clustering | Grain storage | Streams | Secrets |
|----------|---------|-----------|---------------|---------|---------|
| `local` | `DigitalBrain.Runtime.Orleans` | localhost | files under `<Home>` | memory | machine key (DPAPI or a 0600 key file) |
| `local+cloud` | `DigitalBrain.Runtime.Orleans` | localhost | files under `<Home>` | memory | machine key, plus an https server address |
| `cloud` | `DigitalBrain.Runtime.Orleans.Azure` | Azure Table | Azure Blob | Azure Queue | Key Vault |

`cloud` reads `IntoChatOS:Cloud:Storage` (an account URI or a connection string) and `IntoChatOS:Cloud:KeyVault`.
Register it with `AddCloudTopology()` and pass the result to `UseTopology`.

## Configuration precedence

Highest first:

1. Command line
2. Environment variables
3. `<Home>/settings.json`
4. Topology defaults
5. Module defaults (options classes)
