# IntoChat.OS.Modules.Flutter

Flutter module integration for the neuron kernel.

Module id: `flutter`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Google/Flutter/tests/IntoChat.OS.Modules.Flutter.Tests.Hosting/IntoChat.OS.Modules.Flutter.Tests.Hosting.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:flutter:Options` (environment prefix `IntoChatOS__Modules__flutter__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Hosting` | `FlutterHostingOptions` | `FlutterHostingOptions { Kind = Window, ResourceName = , DeviceTarget = auto, ShellName = desk, ChatName = main, FlutterCommand = , WorkingDirectory = , ReleaseBuild = False, WebContainer = False, AndroidTransport = Wireless, AndroidDevice = , IosTransport = Auto, IosDevice = , IosUiBase =  }` | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Contracts` (`IAgentContext`, `IAgent`, `IAppRun`, `IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IApprovals`, `IArtCraftEngine`, `IBrainOwners`, `IBrainToolsConnection`, `IBuildChoices`, `ICapabilitySatisfaction`, `IClientGrants`, `ICodeBlockRun`, `ICodeGraph`, `ICodeOrchestrationAstSanitizer`, `ICodeOrchestrationModel`, `ICodebaseTree`, `ICompiledApps`, `ICompute`, `IConversation`, `ICreator`, `IData`, `IDebuggerReads`, `IDeployedApp`, `IDeployedApps`, `IDesktopRecords`, `IDrafter`, `IEmbeddingModel`, `IEngineerBuildVerifier`, `IEngineerFlow`, `IEngineerPlanner`, `IEngineerRuns`, `IEngineeringHarness`, `IFeatureReconciler`, `IFileShares`, `IFiles`, `IFlowStarter`, `IFlows`, `IFocus`, `IFriends`, `IGroupChat`, `IImageGeneration`, `IImageGenerator`, `IInoConversations`, `IInoRecords`, `IInstalledApps`, `IInstalledModules`, `IIntentUsage`, `IKnowledgeStore`, `ILLM`, `ILegacyPreferenceField`, `ILiveEnvironment`, `ILiveVerifier`, `IMarketplace`, `IMcpConnectionSeedProvider`, `IMcpToolConnection`, `IMemory`, `IModelCatalog`, `IModelInference`, `INotifications`, `IPolicyModel`, `IPosts`, `IPresence`, `IProfileContextWriter`, `IProfile`, `IRoom`, `IRooms`, `IRoslyn`, `IRouter`, `IRuns`, `ISandboxRuntimeBridge`, `IScreenDirectory`, `IScreenTargetResolver`, `ISecurityPosture`, `ISelfImprovementLoop`, `ISessionJournal`, `ISessionList`, `ISharedWithMe`, `IStudio`, `ISynapseDefinitions`, `ISynapseStudio`, `ISystemOne`, `ITestVerificationNeuron`, `ITestVerificationService`, `IToneObserver`, `IToolSource`, `ITraceReader`, `IUiIntrospector`, `IUiRootProvider`, `IUnitVerifier`, `IUserSettings`, `IVectorIndex`, `IVennSystemOne`, `IView`, `IViewport`, `IWebBrowser`, `IWorkflowRun`, `IWorkingMemory`, `IWorkspaceFiles`, `IWorkspace`, `IWorktreeRoslyn`).
Hosting: runs in process.
<!-- END GENERATED -->
