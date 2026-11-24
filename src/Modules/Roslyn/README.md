# IntoChat.OS.Modules.Microsoft.Roslyn

Roslyn workspace neuron. MSBuild loads the solution; the grain keeps it.

Module id: `roslyn`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Microsoft/Roslyn/tests/IntoChat.OS.Modules.Microsoft.Roslyn.Tests.Unit/IntoChat.OS.Modules.Microsoft.Roslyn.Tests.Unit.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:roslyn:Options` (environment prefix `IntoChatOS__Modules__roslyn__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Roslyn.Contracts` (`ICodeGraph`, `ICodebaseTree`, `IRoslyn`, `IWorktreeRoslyn`).
Hosting: runs in process.
<!-- END GENERATED -->
