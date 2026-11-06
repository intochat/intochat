# IntoChat.Cloud.Mail

Mail module: Azure Communication Services email behind IMailSender, a log sender for tests and development, and a per-recipient rate neuron. Loaded only when Topology=cloud.

Module id: `mail`.

How to test: this module has no tests project yet.

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:mail:Options` (environment prefix `IntoChatOS__Modules__mail__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `acs-email` | yes |
| `recipient-hashing` | yes |

Contracts: assembly `IntoChat.Cloud.Mail.Contracts` (`IMailRate`, `IMailSender`, `IRecipientHasher`).
Hosting: runs in process.
<!-- END GENERATED -->
