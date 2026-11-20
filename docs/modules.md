# Modules

A module is a DigitalBrain module: a class that derives from `DigitalBrain.Modules.Module` and carries the `[Module]` attribute with its id. The framework defines the term; see the [framework docs](../framework/docs/README.md). In the product, a module adds neurons, contracts, settings and sometimes HTTP endpoints to the kernel.

Each module lives in a folder under `src/Modules` and has its own `README.md`. That README lists the module's options and secrets.

## The modules

| Folder | Module id | What it does |
| --- | --- | --- |
| `AI` | `ai` | Model and embedding clients for several providers, agents, memory and voice transcription. |
| `ArtCraft` | `artcraft` | The PhotoCraft and FilmCraft apps: their MCP servers and web builds. Needs a container. |
| `Azure` | `azure`, `aspire` | Azure as a connection: resource groups, storage, vaults and apps through Azure Resource Manager. `AspireModule` reads runtime resources, logs and traces. |
| `Catalog` | `apps` | App manifests, install, versions, approvals, flows and the studio that builds units. |
| `Circles` | `circles` | Circles: brains that share apps, a chat and a share of the owner's compute. |
| `ClickHouse` | `clickhouse` | Read-only ClickHouse queries and live query tables. Needs a container. |
| `GitHub` | `github` | GitHub repository connection neurons. |
| `Google` | `google-auth`, `google-drive`, `gmail` | Google sign-in shared by Drive and Gmail; Drive as storage; Gmail. |
| `Mcp` | `mcp`, `tools` | Any Streamable-HTTP MCP server as a tool source, and the per-brain registry of tool sources. |
| `Notifications` | `notifications` | A durable notification inbox per brain, with an HTTP API. |
| `Playwright` | `playwright` | Browser sessions and page tools. |
| `Postgres` | `postgres` | Read-only PostgreSQL queries, schema discovery and table neurons. Needs a container. |
| `Roslyn` | `roslyn` | A Roslyn workspace over a solution: symbols, references, diagnostics and checked edits. |
| `Salesforce` | `salesforce` | One org connection: bounded queries and confirmed record writes. |
| `Shell` | `flutter` | The kernel side of the Flutter shell: views, screens, chat, workspace and settings neurons. Its contracts assembly (`IntoChat.OS.Modules.Shell.Contracts`) lives in `src/Modules/Shell/Contracts`. |
| `Storage` | `local-disk` | Local disks as storage: allowed roots, volume quota and confined file access. |
| `Time` | `time` | Timers and persistent periodic reminders. |
| `Windows` | `windows` | A Windows PC as a connection: its files and console, inside allowed roots. Loaded only on Windows. |

The kernel also carries modules of its own: `OrleansModule` and `HttpModule` (id `web`) in `src/IntoChat.OS.Kernel`, and `IdentityModule` in `src/Modules/Identity`.

## How a module is added

1. Create a folder under `src/Modules` with a project whose assembly name starts with `IntoChat.OS.Modules.`. The framework has a template; see [module template](../framework/docs/modules.md).
2. Add a public module class with a parameterless constructor, the `[Module]` attribute and a static `Manifest` of type `ModuleManifest`. The manifest names what the module provides, requires and needs, and its settings and secrets.
3. Reference the project from `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj`.

Nothing else registers it. `OperatingSystemModules` walks the assemblies the kernel references and takes every module class it finds there. It does not scan the folder beside the process.

A module can declare more through static members that the host reads:

- `RequiresContainer`: the module is left out of the in-process composition used by tests.
- `LoadsUnder`: the module loads only under the topologies it accepts.
- `Contracts`: the assembly that holds the module's contracts.
- `Integration` or `Integrations`: the secrets and settings a connection of this module asks for.

A module's options bind from its own configuration section, for example `IntoChatOS:Modules:ai:Options`. Setting `Enabled` to `false` in a module's section turns the module off.

## Connections

A connection is a neuron. A connector derives from the framework's `ConnectionNeuron<TState>`. A connection key has three parts: the brain, the connector and the connection, built by `ConnectionKeys`.

The connection keeps its own credentials. Code that needs them runs inside `UseCredentialAsync`, which refuses to return a raw credential or OAuth grant. Callers get results, never tokens.

## Capabilities

A capability is a contract that more than one connection can implement. Storage is the main example: `LocalDiskModule`, `DriveModule` and `WindowsModule` each list `Capability.Storage.V1` in `Provides`.

Code asks `ICapabilityBroker` for a capability, not for a connector:

- `Resolve` returns the one implementation of a single-connection capability, such as secrets or identity.
- `ResolveAll` returns every connection of the brain that implements the capability, in Settings order.

`KernelCapabilityConnections` is the kernel's side of this. It lists a brain's connections in Settings order and binds the ones the broker picks. It binds only the brain's own connection keys, or a key shared with the caller through a recorded binding. When no connection satisfies a capability, it raises a connection request for you to answer.

A caller marked as a sandbox or test caller is bound to isolated storage, labelled `SANDBOX / TEST CREDENTIALS`.
