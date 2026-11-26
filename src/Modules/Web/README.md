# IntoChat.OS.Modules.Web

Web tools module: web search and vetted public web page reading.

Module id: `webtools`.

How to test: `dotnet test --project tests/IntoChat.OS.Tests.Unit/IntoChat.OS.Tests.Unit.csproj --filter-class "*WebModuleOnTestOS"`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:webtools:Options` (environment prefix `IntoChatOS__Modules__webtools__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `MaxMarkdownChars` | `Int32` | `32000` | no |
| `Provider` | `SearchProvider` | `Off` | no |
| `SearxngUrl` | `Uri` | unset | no |

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `ApiKey` | yes |
| `ApiKey` | yes |

Contracts: assembly `IntoChat.OS.Modules.Web` (`IWebRead`, `IWebSearch`).
Hosting: runs in process.
<!-- END GENERATED -->
