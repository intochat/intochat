# IntoChat.OS.Modules.Microsoft.Roslyn

Roslyn workspace neuron. MSBuild loads the solution; the grain keeps it.

Module id: `roslyn`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/Microsoft/Roslyn/tests/IntoChat.OS.Modules.Microsoft.Roslyn.Tests.Unit/IntoChat.OS.Modules.Microsoft.Roslyn.Tests.Unit.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:roslyn:Options` (environment prefix `IntoChatOS__Modules__roslyn__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Contracts` (`IAgentContext`, `IAgent`, `IAppRun`, `IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IApprovals`, `IArtCraftEngine`, `IBrainOwners`, `IBrainToolsConnection`, `IBuildChoices`, `ICapabilitySatisfaction`, `IClientGrants`, `ICodeGraph`, `ICodeOrchestrationAstSanitizer`, `ICodeOrchestrationModel`, `ICodeOrchestration`, `ICodeSandbox`, `ICodebaseTree`, `ICompiledApps`, `ICompute`, `IConsentedFolderRootProvider`, `IConversation`, `ICreator`, `IData`, `IDebuggerReads`, `IDeployedApp`, `IDeployedApps`, `IDesktopRecords`, `IDesktopShell`, `IDrafter`, `IEmbeddingModel`, `IEngineerBuildVerifier`, `IEngineerCandidateHostVerifier`, `IEngineerFlow`, `IEngineerPlanner`, `IEngineerRuns`, `IEngineeringHarness`, `IFeatureReconciler`, `IFileShares`, `IFiles`, `IFlowStarter`, `IFlows`, `IFocus`, `IFriends`, `IGroupChat`, `IImageGeneration`, `IImageGenerator`, `IInoConversations`, `IInoRecords`, `IInstalledApps`, `IInstalledModules`, `IIntentUsage`, `IKnowledgeStore`, `ILLM`, `ILegacyPreferenceField`, `ILiveEnvironment`, `ILiveVerifier`, `IMarketplace`, `IMcpConnectionSeedProvider`, `IMcpToolConnection`, `IMemory`, `IModelCatalog`, `IModelInference`, `INotifications`, `IPolicyModel`, `IPosts`, `IPresence`, `IProfileContextWriter`, `IProfile`, `IRoomDriver`, `IRoom`, `IRooms`, `IRoslyn`, `IRouter`, `IRuns`, `ISandboxRuntimeBridge`, `IScreenDirectory`, `IScreenTargetResolver`, `ISecurityPosture`, `ISelfImprovementLoop`, `ISessionJournal`, `ISessionList`, `ISharedWithMe`, `IStudio`, `ISynapseDefinitions`, `ISynapseStudio`, `ISystemOne`, `ITestVerificationNeuron`, `ITestVerificationService`, `IToneObserver`, `IToolSource`, `ITraceReader`, `IUiIntrospector`, `IUiRootProvider`, `IUnitVerifier`, `IUserSettings`, `IVectorIndex`, `IVennSystemOne`, `IView`, `IViewport`, `IWebBrowser`, `IWorkflowRun`, `IWorkingMemory`, `IWorkspaceFiles`, `IWorkspace`, `IWorktreeRoslyn`).
Hosting: runs in process.
<!-- END GENERATED -->
