# IntoChat.OS.Modules.Apps

App manifests, install, versions and Save as app. Invariants are rules in `Rules/` and `DB0040` is on.

Module id: `apps`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/IntoChat.OS/Apps/tests/IntoChat.OS.Modules.Apps.Tests.Integration/IntoChat.OS.Modules.Apps.Tests.Integration.csproj`

Documentation: https://github.com/intochat/os.docs (modules).

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:apps:Options` (environment prefix `IntoChatOS__Modules__apps__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Modules.Catalog` (`IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IApprovals`, `IArtCraftEngine`, `IBuildChoices`, `ICapabilitySatisfaction`, `IConsentFacet`, `IDataAnalyticsWorker`, `IDebuggerReads`, `IDesktopRecords`, `IDrafter`, `IFeatureMigration`, `IFeatureReconciler`, `IFilesNeuron`, `IFilesVfsService`, `IFilesWorkspace`, `IFlowStarter`, `IFlows`, `IInoConversations`, `IInoData`, `IInoFiles`, `IInoFocus`, `IInoRecords`, `IInoSettings`, `IInoStudio`, `IInstalledModules`, `IMarketplace`, `IPolicyModel`, `IPresence`, `IProfileContextWriter`, `IProfile`, `ISandboxRuntimeBridge`, `ISandboxSignalRouter`, `ISynapseStudio`, `ITraceReader`, `IUserSettings`, `IVfsFacet`, `IVfsNeuron`, `IViewFacet`, `IWithFilesVfs`).
Hosting: runs in process.
<!-- END GENERATED -->
