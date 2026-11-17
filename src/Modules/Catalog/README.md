# IntoChat.OS.Modules.Apps

App manifests, install, versions and Save as app. Invariants are rules in `Rules/` and `DB0040` is on.

Module id: `apps`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/IntoChat.OS/Apps/tests/IntoChat.OS.Modules.Apps.Tests.Integration/IntoChat.OS.Modules.Apps.Tests.Integration.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:apps:Options` (environment prefix `IntoChatOS__Modules__apps__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Catalog.Contracts` (`IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IApprovals`, `IArtCraftEngine`, `IBrainOwners`, `IBuildChoices`, `ICapabilitySatisfaction`, `IClientGrants`, `ICodeBlockRun`, `IDataAnalyticsWorker`, `IDebuggerReads`, `IDesktopRecords`, `IDrafter`, `IFeatureReconciler`, `IFileShares`, `IFilesNeuron`, `IFilesWorkspace`, `IFlowStarter`, `IFlows`, `IFriendLinks`, `IInoConversations`, `IInoData`, `IInoFiles`, `IInoFocus`, `IInoRecords`, `IInoSettings`, `IInoStudio`, `IInstalledModules`, `IMarketplace`, `IPresence`, `IProfileContextWriter`, `IProfile`, `IRuns`, `ISandboxRuntimeBridge`, `ISharedWithMe`, `ISynapseDefinitions`, `ISynapseStudio`, `ITraceReader`, `IUserSettings`, `IVfsNeuron`).
Hosting: runs in process.
<!-- END GENERATED -->
