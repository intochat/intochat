# IntoChat.OS.Modules.Infrastructure

Consolidated OS infrastructure connectors and drivers (Azure, ClickHouse, PostgreSQL, LocalDisk storage, Time, Notifications, and Windows edge).

How to test: `dotnet test --project tests/IntoChat.OS.Tests.Unit -- --filter-namespace "IntoChat.OS.Modules.*"`

<!-- generated:manifest -->
<!-- /generated:manifest -->

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:azure:Options` (environment prefix `IntoChatOS__Modules__azure__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Infrastructure` (`IAzureArm`, `IAzure`, `IClickHouse`, `IConsole`, `IFileSystem`, `ILocalDisk`, `INotificationsInbox`, `IPostgresServer`, `IPulumiEngine`, `IPulumi`, `IReminder`, `ITable`, `ITable`1`, `ITimer`, `IWindowsFolderGateway`, `IWindows`).
Hosting: runs in process.
<!-- END GENERATED -->
