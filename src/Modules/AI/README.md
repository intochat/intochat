# IntoChat.OS.Modules.AI

AI module: multi-provider LLM and embedding clients, agents, and voice transcription.

Module id: `ai`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/AI/tests/IntoChat.OS.Modules.AI.Tests.Hosting/IntoChat.OS.Modules.AI.Tests.Hosting.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:ai:Options` (environment prefix `IntoChatOS__Modules__ai__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Endpoint` | integration setting | unset | no |
| `Endpoint` | integration setting | unset | no |
| `Endpoint` | integration setting | unset | no |
| `Endpoint` | integration setting | unset | no |
| `Endpoint` | integration setting | unset | no |
| `Endpoint` | integration setting | unset | no |

Secrets (names only; values are sealed per brain, never configuration):

| Secret | Required |
| --- | --- |
| `ApiKey` | yes |
| `ApiKey` | yes |
| `ApiKey` | yes |
| `ApiKey` | yes |
| `ApiKey` | yes |
| `ApiKey` | yes |

Contracts: assembly `IntoChat.OS.Contracts` (`IAgentContext`, `IAgent`, `IAppRun`, `IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IApprovals`, `IArtCraftEngine`, `IBrainOwners`, `IBrainToolsConnection`, `IBuildChoices`, `ICapabilitySatisfaction`, `IClientGrants`, `ICodeBlockRun`, `ICodeGraph`, `ICodeOrchestrationAstSanitizer`, `ICodeOrchestrationModel`, `ICodebaseTree`, `ICompiledApps`, `ICompute`, `IConversation`, `ICreator`, `IData`, `IDebuggerReads`, `IDeployedApp`, `IDeployedApps`, `IDesktopRecords`, `IDrafter`, `IEmbeddingModel`, `IEngineerBuildVerifier`, `IEngineerFlow`, `IEngineerPlanner`, `IEngineerRuns`, `IEngineeringHarness`, `IFeatureReconciler`, `IFileShares`, `IFiles`, `IFlowStarter`, `IFlows`, `IFocus`, `IFriends`, `IGroupChat`, `IImageGeneration`, `IImageGenerator`, `IInoConversations`, `IInoRecords`, `IInstalledApps`, `IInstalledModules`, `IIntentUsage`, `IKnowledgeStore`, `ILLM`, `ILegacyPreferenceField`, `ILiveEnvironment`, `ILiveVerifier`, `IMarketplace`, `IMcpConnectionSeedProvider`, `IMcpToolConnection`, `IMemory`, `IModelCatalog`, `IModelInference`, `INotifications`, `IPolicyModel`, `IPosts`, `IPresence`, `IProfileContextWriter`, `IProfile`, `IRoom`, `IRooms`, `IRoslyn`, `IRouter`, `IRuns`, `ISandboxRuntimeBridge`, `IScreenDirectory`, `IScreenTargetResolver`, `ISecurityPosture`, `ISelfImprovementLoop`, `ISessionJournal`, `ISessionList`, `ISharedWithMe`, `IStudio`, `ISynapseDefinitions`, `ISynapseStudio`, `ISystemOne`, `ITestVerificationNeuron`, `ITestVerificationService`, `IToneObserver`, `IToolSource`, `ITraceReader`, `IUiIntrospector`, `IUiRootProvider`, `IUnitVerifier`, `IUserSettings`, `IVectorIndex`, `IVennSystemOne`, `IView`, `IViewport`, `IWebBrowser`, `IWorkflowRun`, `IWorkingMemory`, `IWorkspaceFiles`, `IWorkspace`, `IWorktreeRoslyn`).
Hosting: runs in process.
<!-- END GENERATED -->
