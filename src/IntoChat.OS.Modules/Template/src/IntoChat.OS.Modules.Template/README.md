# IntoChat.OS.Modules.Template

Canonical template module reference implementation.

Module id: `template`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Template/tests/IntoChat.OS.Modules.Template.Tests.Unit/IntoChat.OS.Modules.Template.Tests.Unit.csproj`

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:template:Options` (environment prefix `IntoChatOS__Modules__template__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `DefaultPrefix` | `String` | `Echo` | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Template.Contracts` (`ITemplateNeuron`).
Hosting: runs in process.
<!-- END GENERATED -->
