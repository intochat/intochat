# Architecture

IntoChat is built on DigitalBrain, the framework in [`framework/`](../framework/README.md). The framework defines neuron, signal, synapse, app and module; see the [framework docs](../framework/docs/README.md). This page describes the product on top of it.

The product has four parts.

| Part | What it is | Where it lives |
| --- | --- | --- |
| Kernel | The host process: it composes the modules, hosts the neurons and serves the shell. | `src/IntoChat.OS.Kernel`, `src/IntoChat.OS.Sandbox` |
| System apps | The apps compiled into the product. | `src/IntoChat.OS.Apps` |
| Modules | Capabilities and connectors the kernel composes. | `src/Modules` |
| Shell | What you see: the Flutter client and the desktop launcher. | `ui`, `src/IntoChat.OS.Desktop` |

`src/IntoChat.OS.Framework` holds product-side hosting code (identity, connections, `SiloAccessCallFilter`, `NeuronsGrpcService`). `src/IntoChat.OS.AppHost` (`IntoChat.OS.Aspire.Hosting`) provides .NET Aspire helpers (`AddIntoChatOS` and `AddIntoChatOSContainer` in `Hosting/IntoChatOSHostingExtensions.cs`, and `AddKernel` in `Hosting/Composition/OperatingSystemHosting.cs`). `src/Modules/Shell/Contracts` (`IntoChat.OS.Modules.Shell.Contracts`) holds the shell wire contracts and signals separately from `src/Modules/Shell`.

## Kernel

`src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Program.cs` delegates to `KernelHost.RunAsync(args)` in `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/KernelHost.cs` (which also accepts `rootAssembly`, `configureServer`, `beforeAccountSession`, `configureApp`, and `cancellationToken` so external hosts can compose additional modules and hooks):

1. When `AzureInstaller.IsInstall(args)` matches, it discovers modules with `OperatingSystemModules.Discover(rootAssembly)` and runs `AzureInstaller.Run` before exiting.
2. Otherwise it applies `LocalRunDefaults.Apply`, reads `IntoChatOS:Topology` (`IntoChatOSNames.TopologyKey`), and discovers modules via `OperatingSystemModules.WithWindowsEdge(rootAssembly)` on Windows or `OperatingSystemModules.Discover(rootAssembly)` elsewhere, keeping only those where `module.LoadsUnder(topology)` is true.
3. `builder.AddIntoChatOSServer` configures the Orleans silo, the DigitalBrain runtime, `server.AddOperatingSystem(catalog)` (modules and shipped OS apps), and any `configureServer` callback (enabling the Orleans dashboard when configured); `builder.Services.AddIntoChatOSMcp()` registers MCP services.
4. `app.MapIntoChatOSServer(beforeAccountSession)` maps the static web shell, HTTP endpoints, health probes, and gRPC endpoints; `app.MapIntoChatOSMcp()` maps `/mcp` plus the brain-scoped `/brains/{brainId}/mcp` route (via `OsBrainRoutes.Group` with `GuestSurface.Door`).

The `IntoChatOS:Topology` setting picks clustering, state stores, streams, reminders and secrets.

The sandbox worker in `src/IntoChat.OS.Sandbox` runs app code made at run time outside the kernel process. See [sandbox and live behaviors](sandbox-and-live-behaviors.md).

## System apps

Each system app has a folder in `src/IntoChat.OS.Apps`, for example `Ino`, `Creator`, `Desktop`, `Settings` and `Tasks`. A system app is a neuron. `InoApp` and `CreatorApp` derive from the framework's `Screen<TState>`: the app keeps a state record and renders it as a `View` tree. C# decides what is on screen; Flutter only draws it.

`ShippedShellAppsModule` (id `os-apps`) carries these neurons into the kernel. When a brain is created, `OsDefaults` installs the default apps into it. `src/IntoChat.OS.Apps/Units` holds the unit sources the product ships; `ShippedUnits` deploys them like any unit made at run time.

### Creator

`CreatorApp` (`src/IntoChat.OS.Apps/Creator`) is the place to make units and watch them run. It shows the brain as a graph, with an inspector for the selected node, edge or trace. It reads everything from the kernel. Its operations are signals:

- `CreatorMake` passes a goal to `IInoStudio`, which starts a build.
- `CreatorChange` starts a build flow over a unit's current files through `IFlows`.
- `CreatorScenario` runs a unit's kept scenarios and shows the transcript.
- `CreatorSeed` deploys a shipped unit through `ISynapseStudio`.
- `CreatorApprove` and `CreatorReject` resolve a pending approval through `IConfirmationInbox`.
- `CreatorSelect`, `CreatorTrace` and `CreatorReplay` drive the inspector and step through a trace.
- `CreatorShare` publishes the selected unit to your profile.

## Modules

A module adds neurons, contracts and settings to the kernel. Each lives in a folder under `src/Modules`. See [modules](modules.md).

## Shell

`ui` is the Flutter client. `ui/lib/desktop` holds the desktop and window frame, `ui/lib/workspace` the app surfaces, and `ui/lib/src/core` the transport to the kernel.

`src/IntoChat.OS.Desktop` is the launcher for one machine. It starts the kernel and the Flutter shell, and keeps its files under `~/.intochat`.

## How a request flows

1. **Shell.** You act in a window. `NeuronChannel` (`ui/lib/src/core/neuron_channel.dart`) sends a call over gRPC, or gRPC-Web in a browser. The call names a brain, a contract, a key, a method and JSON arguments.
2. **Kernel edge.** `NeuronsGrpcService` receives it. `BrainCall` checks that the caller may enter that brain, then `EdgeAuthority` builds the `Caller` (an `Actor` plus brain, conversation, via and inside) and enters it for the call. Only `EdgeAuthority` factories build a `Caller`; the kernel is entered only through `EdgeAuthority.EnterKernel` (in the OS, `PlatformCall.AsKernel`). Reminders, timers and activations, and the work they defer, run as `Actor.Scheduler` of the brain.
3. **Neuron surface.** `OsNeuronSurface` (backed by `NeuronSurface` in DigitalBrain) finds the contract, picks the method by name and argument count, reads the JSON arguments and calls the neuron.
4. **Neuron.** The neuron runs in the Orleans silo. It may change its state and publish signals. `SiloAccessCallFilter` is the enforcement point for who may call what: it reads the caller from `ICallerAmbient`, refuses a call with no caller on every host, refuses a call into another brain, then asks each `ICallFilterStage` in order.
5. **Back.** The result returns as JSON in the reply. The shell also holds a `Watch` stream on the neurons it shows. A change signal tells the view to read again, and Flutter draws the new `View`.

The same `OsNeuronSurface` serves two other callers. The MCP tools in `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Mcp/Tools` use it, and so does Ino; see [Ino and memory](ino-and-memory.md).
