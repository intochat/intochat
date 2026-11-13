# IntoChat.OS.Modules.Identity

identity@1 and secrets@1: accounts, brains, grants, sessions and the vault. Invariants are rules in `Rules/` and `DB0040` is on.

Module id: `authority`.

How to test: this module has no tests project yet.

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:identity:Options` (environment prefix `IntoChatOS__Modules__identity__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Framework` (`IBrainBirths`, `IBrainEntries`, `IBrainMembership`, `IBrainOwnershipCounters`, `IBrainProviderKeys`, `ICallBindings`, `ICallFilterStage`, `ICallFilter`, `IChatIndex`, `IChat`, `IClusterMembershipFeed`, `ICluster`, `ICompute`, `IConnectionBindings`, `IContactsNeuron`, `ICreationHandler`, `IEdgeGrantsCache`, `IField`, `IGrantsNeuron`, `IHandleDirectory`, `IHostedAppTypes`, `IIdentityClient`, `IIdentityDirectory`, `IIntegration`, `IIntoChatOSBrains`, `IKernel`, `IKeyWrapper`, `ILicenseCheck`, `IMachineIdentity`, `IMachineKeyStore`, `IModuleTypeSource`, `IRevisioned`1`, `IScreenDirectory`, `ISecretRedactor`, `ISecretValueSource`, `ITopologyModule`, `IView`, `IViewport`, `IWebBrowser`, `IWorkspace`).
Hosting: runs in process.
<!-- END GENERATED -->
