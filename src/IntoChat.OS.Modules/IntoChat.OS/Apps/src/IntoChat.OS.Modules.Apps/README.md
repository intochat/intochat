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

Contracts: assembly `IntoChat.OS.Modules.Apps.Contracts` (`IAppRuntime`, `IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IArtCraftEngine`, `IFeatureReconciler`, `IFilesNeuron`, `IFilesWorkspace`, `IFlowStarter`, `IFlows`, `IInoData`, `IInoFiles`, `IInoSettings`, `IInoStudio`, `IInstalledModules`, `IMarketplace`, `IProfileContextWriter`, `IProfile`, `IScriptSandbox`, `ISideload`, `ISynapseStudio`, `IUserSettings`, `IVfsNeuron`).
Hosting: runs in process.
<!-- END GENERATED -->
