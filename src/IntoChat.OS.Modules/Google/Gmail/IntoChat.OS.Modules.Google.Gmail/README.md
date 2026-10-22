# IntoChat.OS.Modules.Google.Gmail

Google module: hosted Gmail MCP tools, browser sign-in and draft previews.

Module id: `gmail`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Google/Gmail/tests/IntoChat.OS.Modules.Google.Gmail.Tests.Integration/IntoChat.OS.Modules.Google.Gmail.Tests.Integration.csproj`

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:gmail:Options` (environment prefix `IntoChatOS__Modules__gmail__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Audience` | integration setting | unset | yes |
| `ServiceAccountEmail` | integration setting | unset | yes |

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `ClientId` | yes |
| `ClientSecret` | yes |

Contracts: assembly `IntoChat.OS.Modules.Google.Gmail` (`IGmail`).
Hosting: runs in process.
<!-- END GENERATED -->
