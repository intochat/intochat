# IntoChat.OS.Modules.Notifications

Durable per-brain notification inbox and HTTP API.

Module id: `notifications`.

How to test: this module is tested in `tests/IntoChat.OS.Tests.Unit/Modules/Notifications/`.

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:notifications:Options` (environment prefix `IntoChatOS__Modules__notifications__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `MaxItems` | `Int32` | `200` | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Notifications` (`INotificationsInbox`).
Hosting: runs in process.
<!-- END GENERATED -->
