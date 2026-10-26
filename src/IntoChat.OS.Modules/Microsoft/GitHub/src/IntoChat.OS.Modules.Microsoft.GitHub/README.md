# IntoChat.OS.Modules.Microsoft.GitHub

GitHub repository connection neurons.

Module id: `github`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Microsoft/GitHub/tests/IntoChat.OS.Modules.Microsoft.GitHub.Tests.Hosting/IntoChat.OS.Modules.Microsoft.GitHub.Tests.Hosting.csproj`

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:github:Options` (environment prefix `IntoChatOS__Modules__github__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `AppId` | `Int64` | `0` | no |
| `ClientId` | `String` | `` | no |
| `PublicOrigin` | `Uri` | unset | no |
| `PublicWebhookUrl` | `String` | `` | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Microsoft.GitHub.Contracts` (`IRepository`).
Hosting: runs in process.
<!-- END GENERATED -->
