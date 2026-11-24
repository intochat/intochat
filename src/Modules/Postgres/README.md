# IntoChat.OS.Modules.Postgres

Read-only PostgreSQL queries, schema discovery, and scoped declarative table neurons.

Module id: `postgres`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Postgres/tests/IntoChat.OS.Modules.Postgres.Tests.Hosting/IntoChat.OS.Modules.Postgres.Tests.Hosting.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:postgres:Options` (environment prefix `IntoChatOS__Modules__postgres__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `AdminConnection` | no |
| `ConnectionString` | yes |
| `admin-connection` | yes |
| `connection-string` | yes |

Contracts: assembly `IntoChat.OS.Modules.Postgres` (`IPostgresServer`, `ITable`, `ITable`1`).
Hosting: needs a container.
<!-- END GENERATED -->
