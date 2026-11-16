# IntoChat.Cloud

IntoChat Cloud modules: Auth, Blob storage, Circle relay, Hosting, Lobby, Localization, Mail, and Organisations.

Module id: `auth`.

How to test: this module has no tests project yet.

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:auth:Options` (environment prefix `IntoChatOS__Modules__auth__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `auth-hashing` | yes |
| `google-client-id` | yes |
| `google-client-secret` | yes |
| `signin-issuer` | yes |

Contracts: assembly `IntoChat.Cloud` (`IAccountAuth`, `IAccountProvisioning`, `IAuthAuditLog`, `IAuthCode`, `IAuthCredential`, `IAuthEmailIndex`, `IAuthHostSeam`, `IAuthProfile`, `IAuthSessions`, `IAuthSystemAudit`, `ICircleConnection`, `ICloudSignIn`, `ICodeSource`, `IComputeDelegate`, `IInviteToken`, `IIpThrottle`, `ILobbyAuth`, `ILobbyTokenKey`, `ILocalizer`, `IMailHost`, `IMailRate`, `IMailSender`, `INonceCache`, `IOrgMembership`, `IOrganisationDirectory`, `IOrganisationHost`, `IOrganisationIdentity`, `IOrganisationInvites`, `IOrganisationMail`, `IOrganisation`, `IRecipientHasher`).
Hosting: runs in process.
<!-- END GENERATED -->
