# IntoChat.Aspire

The Aspire component: telemetry of the running app (`IAspire`), candidate builds (`ICandidate`, `ICandidates`), deployment rolls (`IDeployment`) and candidate verification. Contracts are in `Contracts/` (assembly `IntoChat.Aspire.Contracts`), neurons in `Neurons/`. Packages: `IntoChat.OS.Aspire`, `IntoChat.OS.Aspire.Contracts`.

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
