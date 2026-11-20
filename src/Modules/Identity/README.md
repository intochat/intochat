# IntoChat.OS.Modules.Identity

identity@1 and secrets@1: accounts, brains, grants, sessions and the vault. Invariants are rules in `Rules/` and `DB0040` is on.

Module id: `identity`.

The assembly also still carries the OS host layer (service defaults, configuration names, logging, the sandbox runners and the Orleans client) under the old `IntoChat.OS.Framework` namespaces, until that layer moves to a project of its own.

How to test: this module has no tests project yet.

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:identity:Options` (environment prefix `IntoChatOS__Modules__identity__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Identity` (`IBrainBirths`, `IBrainEntries`, `IBrainProviderKeys`, `ICallFilterStage`, `IContactsNeuron`, `IEdgeGrantsCache`, `IGrantsNeuron`, `IHandleDirectory`, `IIdentityClient`, `IIdentityDirectory`, `IIntegration`, `IKeyWrapper`, `IPresenceProof`).
Hosting: runs in process.
<!-- END GENERATED -->
