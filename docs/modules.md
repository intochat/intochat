# Modules

A module is a DigitalBrain module: a class that derives from `DigitalBrain.Modules.Module` and carries the `[Module]` attribute with its id. The framework defines the term; see the [framework docs](../framework/docs/README.md). In the product, a module adds neurons, contracts, settings and sometimes HTTP endpoints to the kernel.

Each module lives in a folder under `src/Modules` and has its own `README.md`. That README lists the module's options and secrets.

Modules and components coexist. A component (see [concepts](concepts.md)) is the newer layout and replaces a module and its system app together, one at a time; only `Aspire` has moved, and every module in the table below is still in `src/Modules`. A component differs from a module in layout:

- It lives in `src/Components/<Name>`: `IntoChat.<Name>.csproj` (and, when split, its contracts in `Contracts/IntoChat.<Name>.Contracts.csproj`; `src/Components/Aspire` currently keeps its contracts in `IntoChat.Aspire.csproj`). Assemblies are `IntoChat.<Name>` (and `IntoChat.<Name>.Contracts` when split); the package ids are `IntoChat.OS.Components.<Name>` and `IntoChat.OS.Components.<Name>.Contracts`.
- It owns its app screens in `Screens/` and registers them from its module class, so no project in `src/IntoChat.OS.Apps` carries them.
- Any AppHost code is in `Hosting/`, which the component's own project does not compile; the AppHost includes it through a linked item.
- Its unit tests are in `tests/IntoChat.OS.Tests.Unit/Components/<Name>`.
- A component's implementation project references other modules and components only through their `Contracts` projects (`ComponentFacts`); exceptions are listed there with a cap and in `OWED.md`. The reverse direction, a module referencing a component's implementation project, is intended to go through `Contracts` too but is not yet checked.

The module class is the same: it derives from `DigitalBrain.Modules.Module`, carries `[Module]` and keeps its id (`aspire` for `AspireModule`). The first component is `src/Components/Aspire`; copy its shape, and read `ComponentFacts` for the rules that are checked.

## The modules

Shared module contracts live in `IntoChat.OS.Contracts` (`src/IntoChat.OS.Contracts`), except Identity's contracts which remain in `IntoChat.OS.Modules.Identity.Contracts` (`src/Modules/Identity/Contracts`).

| Folder | Module id(s) | What it does |
| --- | --- | --- |
| `AI` | `ai`, `devices` | Single `ModelCard` catalog (`KnownModels`), role-based model routing (`IModelCatalog`, `ILLM`), agents, memory, voice (dictation, read-aloud and voice mode on MEAI speech and realtime clients), and this machine's hardware, local model runtimes (`FoundryLocal`, `SherpaOnnx`) and model installs. Contracts live in `IntoChat.OS.Contracts` (`src/IntoChat.OS.Contracts/AI`). |
| `Identity` | `identity`, `local-vault`, `azure-key-vault`, `bitwarden` | Accounts, sessions, grants, pluggable secrets vault and key custody, connections and integrations. Its contracts assembly (`IntoChat.OS.Modules.Identity.Contracts`) lives in `src/Modules/Identity/Contracts`. |
| `Infrastructure` | `azure`, `clickhouse`, `local-disk`, `cloud`, `notifications`, `postgres`, `time`, `windows` | Azure ARM/Pulumi, read-only ClickHouse and PostgreSQL queries (container-backed), local-disk and per-brain workspace storage (`{home}/brains/{brain}/workspace`), durable notifications inbox, timers/reminders, and Windows host files/console (Windows only). |
| `Plugins` | `artcraft`, `github`, `google-auth`, `google-drive`, `gmail`, `mcp`, `tools`, `muncly`, `playwright`, `salesforce`, `treg`, `webtools` | External connectors and tool integrations: ArtCraft (PhotoCraft/FilmCraft, container-backed), GitHub, Google Auth/Drive/Gmail, Streamable-HTTP MCP servers and tool registry, Playwright browser sessions, Salesforce, and web search/reading. |
| `Roslyn` | `roslyn` | A Roslyn workspace over a solution: symbols, references, diagnostics and checked edits. |
| `UI` | `ui` | Shared UI control neurons (`src/Modules/UI/Controls`) and control contracts (`src/Modules/UI/Contracts`). |
| `Workspace` | `apps`, `authoring`, `diagnostics`, `flutter`, `runs` | App manifests, install, versions, approvals, flows, unit authoring (`BuildFlow`, `ISynapseStudio`, `IUnitVerifier`), read-side diagnostics (`IDebuggerReads`, `ITraceReader`), runs read model (`IRuns`, `ISynapseDefinitions`), and the kernel side of the Flutter shell (views, screens, chat, workspace and settings neurons). Contracts live in `IntoChat.OS.Contracts` (`src/IntoChat.OS.Contracts/Catalog`, `src/IntoChat.OS.Contracts/Shell`). |

The kernel also carries modules of its own: `OrleansModule` and `HttpModule` (id `web`) in `src/IntoChat.OS.Kernel`.

## How a module is added

To add a component instead, create `src/Components/<Name>` in the shape of `src/Components/Aspire`; `ComponentFacts` checks the layout. The rest of this section is for a module under `src/Modules`.

1. Create a folder under `src/Modules` with a project whose assembly name starts with `IntoChat.OS.Modules.`. The framework has a template; see [module template](../framework/docs/modules.md).
2. Add a public module class with a parameterless constructor, the `[Module]` attribute and a static `Manifest` of type `ModuleManifest`. The manifest names what the module provides, requires and needs, and its settings and secrets.
3. Reference the project from `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj`.

Nothing else registers it. `OperatingSystemModules` uses compile-time source generation (`[GeneratedModuleRegistry]` / `BuiltInModuleTypes()`) across the kernel and its referenced module and component assemblies, plus `RegisteredModuleAttribute` for external roots; it performs no runtime folder or assembly scanning.

A module can declare more through static members that the host reads:

- `RequiresContainer`: the module is left out of the in-process composition used by tests.
- `LoadsUnder`: the module loads only under the topologies it accepts.
- `Contracts`: the assembly that holds the module's contracts.
- `Integration` or `Integrations`: the secrets and settings a connection of this module asks for.

A module's options bind from its own configuration section, for example `IntoChatOS:Modules:ai:Options`. Values stated in the AppHost chain (`src/IntoChat.OS.AppHost/AppHost.cs`) are defaults that `IntoChatOS:Modules:<id>:Options` overrides; setting `IntoChatOS:Modules:<id>:ReplaceOptions=true` drops them. Setting `Enabled` to `false` in a module's section turns the module off.

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
