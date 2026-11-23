# IntoChat.Aspire

The Aspire component: telemetry of the running app (`IAspire`), candidate builds (`ICandidate`, `ICandidates`), deployment rolls (`IDeployment`) and candidate verification.

Parts: contracts in `Contracts/` (assembly `IntoChat.Aspire.Contracts`, package `IntoChat.OS.Components.Aspire.Contracts`); neurons in `Neurons/` (assembly `IntoChat.Aspire`, package `IntoChat.OS.Components.Aspire`); the screen in `Screens/`; AppHost hosting in `Hosting/`, compiled only into the AppHost.

`IAspire` exposes typed live topology (`Resources()`), console log tails (`ConsoleLog(resource, tail)`), and one guarded writing verb `Execute(resource, command)`. In local run mode, the AppHost hosts a local-only Unix Domain Socket bridge (`AspireBridgeServer`, zero open TCP ports) backed by Aspire's public typed services (`ResourceNotificationService`, `ResourceCommandService`, `ResourceLoggerService`) and passes its socket path via `IntoChatOS__Aspire__BridgeSocket`. `AspireBridgeClient` streams snapshots into `AspireHostStore` and notifies active brains via `IAspireFeed`, publishing `aspire.resource.changed` only on real state/health transitions.

`CommandPolicy` governs `Execute`: a direct person runs any enabled listed command at once; any other caller gets `"pending"` and a confirmation (`aspire.command`), running only after a person approves and re-checking the live topology. A resource is matched by exact instance id or unique model name (replicated model names return `"unknown"` with their replica ids). Any resource belonging to the kernel (`os`, `OperatingSystem`, or this process's instance name) is refused as `"self"`. Commands on `AspireOptions.PersonOnlyCommands` (default `["reset-state"]`) are refused for non-persons as `"person-only"` with no confirmation created. Refusal reasons: `"unavailable"`, `"unknown"`, `"unknown-command"`, `"self"`, `"person-only"`, `"pending"`, `"declined"`, `"failed"`. New AppHost commands must be classified in `CommandClassificationFacts`.

The component's screen is the .NET Aspire app (`app.aspire`, `Screens/AspireApp.cs`, namespace `IntoChat.Aspire.Screens`): a full-bleed browser onto the Aspire dashboard. `AspireModule` registers it (`CompiledAppsRegistration`), so it ships whenever the `aspire` module is loaded.

Module id: `aspire`.

How to test: unit facts in `tests/IntoChat.OS.Tests.Unit/Components/Aspire`, integration facts in `tests/IntoChat.OS.Tests.Integration/Components/Aspire`.

Documentation: `docs/modules.md` and `docs/concepts.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:aspire:Options` (environment prefix `IntoChatOS__Modules__aspire__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Fronts` | integration setting | unset | no |
| `Workdir` | integration setting | unset | yes |

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `CandidateModel` | no |

Contracts: assembly `IntoChat.Aspire.Contracts` (`IAspire`, `ICandidate`, `ICandidates`, `IDeployment`).
Hosting: runs in process.
<!-- END GENERATED -->
