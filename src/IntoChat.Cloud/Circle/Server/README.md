# IntoChat.Cloud.Circle.Server

Circle server module: machine enrollment into a circle, the per-machine circle credential with rotating tokens, and machine proof authentication for the /circle/v1 routes. Loaded only when Topology=cloud.

Module id: `circle-server`.

How to test: this module has no tests project yet.

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:circle-server:Options` (environment prefix `IntoChatOS__Modules__circle-server__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `circle-deployment-key` | yes |

Contracts: assembly `IntoChat.Cloud.Circle.Contracts` (`ICircleConnection`, `IComputeDelegate`, `INonceCache`).
Hosting: runs in process.
<!-- END GENERATED -->
