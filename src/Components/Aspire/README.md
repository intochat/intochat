# IntoChat.Aspire

The Aspire component: telemetry of the running app (`IAspire`), candidate builds (`ICandidate`, `ICandidates`), deployment rolls (`IDeployment`) and candidate verification. Contracts are in `Contracts/` (assembly `IntoChat.Aspire.Contracts`), neurons in `Neurons/`. Packages: `IntoChat.OS.Aspire`, `IntoChat.OS.Aspire.Contracts`.

`IAspire` reads; its one writing verb is Restart (exact live name). A direct person restarts at once. Any other caller gets `pending` and a plan id, and the restart runs once the person approves the confirmation (`aspire.resource.restart`); the outcome arrives as `aspire.resource.restarted` or `aspire.resource.restart-refused`. The kernel and its front are refused.

The component's screen is the .NET Aspire app (`app.aspire`, `Screens/AspireApp.cs`, namespace `IntoChat.Aspire.Screens`): a full-bleed browser onto the Aspire dashboard. `AspireModule` registers it (`CompiledAppsRegistration`), so it ships whenever the `aspire` module is loaded.

Module id: `aspire`.

How to test: unit facts in `tests/IntoChat.OS.Tests.Unit/Components/Aspire`, integration facts in `tests/IntoChat.OS.Tests.Integration/Components/Aspire`.

Documentation: `docs/modules.md` at the repository root.

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
