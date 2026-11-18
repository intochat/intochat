# IntoChat.OS.Modules.Microsoft.Azure

Azure as a connection: ensures resource groups, storage, vaults, environments and apps through Azure Resource Manager, and installs the OS foundation.

Module id: `azure`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Microsoft/Azure/tests/IntoChat.OS.Modules.Microsoft.Azure.Tests.Unit/IntoChat.OS.Modules.Microsoft.Azure.Tests.Unit.csproj`

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

Contracts: assembly `IntoChat.OS.Modules.Azure` (`IAspire`, `IAzureArm`, `IAzure`, `ICandidate`, `ICandidates`, `IDeployment`, `IModuleDeployment`, `IPulumiEngine`, `IPulumi`).
Hosting: runs in process.
<!-- END GENERATED -->
