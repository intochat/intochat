# IntoChat.Cloud.Blob

Azure Blob as storage@1: one container per brain, loaded for cloud and local+cloud only.

Module id: `blob`.

How to test: `dotnet test --project src/IntoChat.Cloud/Modules/Blob/tests/IntoChat.Cloud.Blob.Tests.Integration/IntoChat.Cloud.Blob.Tests.Integration.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:blob:Options` (environment prefix `IntoChatOS__Modules__blob__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `connection-string` | yes |

Contracts: none.
Hosting: runs in process.
<!-- END GENERATED -->
