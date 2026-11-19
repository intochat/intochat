# IntoChat.OS.Modules.Microsoft.Orleans

Orleans as a module: clustering, grain state and reminders as one integration; memory when it is absent.

Module id: `orleans`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Microsoft/Orleans/tests/IntoChat.OS.Modules.Microsoft.Orleans.Tests.Hosting/IntoChat.OS.Modules.Microsoft.Orleans.Tests.Hosting.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:orleans:Options` (environment prefix `IntoChatOS__Modules__orleans__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Microsoft.Orleans.Contracts` (`IClusterMembershipFeed`, `ICluster`).
Hosting: runs in process.
<!-- END GENERATED -->
