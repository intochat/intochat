# IntoChat.OS.Modules.Identity

identity@1 and secrets@1: accounts, brains, grants, sessions and the vault. Invariants are rules in `Rules/` and `DB0040` is on.

Module id: `identity`.

Other projects use the module through its contracts in `Contracts/` (`IntoChat.OS.Modules.Identity.Contracts`): grain interfaces, `IdentityHostOptions`, the grant vocabulary, `GuestSurface`, the presence-proof protocol (`PresenceProof`) and the sign-in configuration keys (`AuthKeys`). Only the kernel references the implementation.

A trusted issuer (`IntoChatOS:Auth:TrustedIssuers`) signs people in with an external ticket at `/identity/external`. An issuer listed with `Services` may also call the install's edge on its own behalf with a service assertion (`IServiceAssertions`), and one listed with `Guests` may send visitors to try the install with a guest ticket.

How to test: this module has no tests project yet.

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:identity:Options` (environment prefix `IntoChatOS__Modules__identity__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Identity.Contracts` (`IBrainBirths`, `IBrainEntries`, `IBrainKeys`, `IBrainProviderKeys`, `ICallFilterStage`, `ICodeBook`, `ICodeIndex`, `ICodeKey`, `IGrantsNeuron`, `IHandleDirectory`, `IIdentityClient`, `IIntegration`, `IServiceAssertions`).
Hosting: runs in process.
<!-- END GENERATED -->
