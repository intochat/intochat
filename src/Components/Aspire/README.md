# IntoChat.Aspire

The Aspire component: telemetry of the running app (`IAspire`), candidate builds (`ICandidate`, `ICandidates`), deployment rolls (`IDeployment`) and candidate verification.

Parts: contracts in `Contracts/` (assembly `IntoChat.Aspire.Contracts`, package `IntoChat.OS.Components.Aspire.Contracts`); neurons in `Neurons/` (assembly `IntoChat.Aspire`, package `IntoChat.OS.Components.Aspire`); the screen in `Screens/`; AppHost hosting in `Hosting/`, compiled only into the AppHost.

`IAspire` reads; its one writing verb is `Restart(resourceName)`. A direct person caller restarts at once. Any other caller gets `"pending"` and a plan id, and the restart runs once the person approves the confirmation (`aspire.resource.restart`); the outcome arrives as `aspire.resource.restarted` or `aspire.resource.restart-refused`. A request the confirmations refuse to ask is `"declined"` at once and nothing waits; a waiting request whose answer never came is dropped as `"declined"` (message `"expired"`) after three times the restart confirmation expiry (the `RestartConfirmationExpiry` option, 5 minutes by default, so 15 minutes). The name may be the one the dashboard shows or the exact name `list_resources` gives (which can end in an 8-character instance suffix); a display name shared by several resources is `"unknown"`, and a resource whose name is or begins with the kernel's name (`os`, `OperatingSystem` or this process's instance name, then `-`, `_` or `.`, any case) is refused as `"self"`, so `os-gateway` and `os-rebuilder` are restarted from the dashboard instead. Refusals are returned values: `"unavailable"`, `"unknown"`, `"self"`, `"declined"`, `"failed"`, `"pending"`.

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
