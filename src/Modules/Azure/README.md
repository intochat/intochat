# IntoChat.OS.Modules.Microsoft.Azure

Azure as a connection: ensures resource groups, storage, vaults, environments and apps through Azure Resource Manager, and installs the OS foundation. The Aspire runtime that used to live here is the Aspire component (`src/Components/Aspire`).

Module id: `azure`.

How to test: unit facts in `tests/IntoChat.OS.Tests.Unit/Modules/Azure`.

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:azure:Options` (environment prefix `IntoChatOS__Modules__azure__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Azure` (`IAzureArm`, `IAzure`, `IModuleDeployment`, `IPulumiEngine`, `IPulumi`).
Hosting: runs in process.
<!-- END GENERATED -->
