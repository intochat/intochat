# IntoChat.OS.Modules.Mcp

Generic MCP client: any Streamable-HTTP MCP server as a tool-source neuron.

Module id: `mcp`.

How to test: `dotnet test --project src/IntoChat.OS.Modules/ModelContextProtocol/Mcp/tests/IntoChat.OS.Modules.Mcp.Tests.Integration/IntoChat.OS.Modules.Mcp.Tests.Integration.csproj`

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:mcp:Options` (environment prefix `IntoChatOS__Modules__mcp__Options__`).

Options:

| Option | Type | Default | Required |
| --- | --- | --- | --- |
| `Servers` | `Dictionary<String, McpServerOptions>` | (collection) | no |

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: assembly `IntoChat.OS.Contracts` (`IAgentContext`, `IAgent`, `IAppRun`, `IAppStorageHook`, `IAppStore`, `IAppStudio`, `IApp`, `IApprovals`, `IArtCraftEngine`, `IBrainOwners`, `IBrainToolsConnection`, `IBuildChoices`, `ICapabilitySatisfaction`, `IClientGrants`, `ICodeBlockRun`, `ICodeGraph`, `ICodeOrchestrationAstSanitizer`, `ICodeOrchestrationModel`, `ICodebaseTree`, `ICompiledApps`, `ICompute`, `IConversation`, `ICreator`, `IData`, `IDebuggerReads`, `IDeployedApp`, `IDeployedApps`, `IDesktopRecords`, `IDrafter`, `IEmbeddingModel`, `IEngineerBuildVerifier`, `IEngineerFlow`, `IEngineerPlanner`, `IEngineerRuns`, `IEngineeringHarness`, `IFeatureReconciler`, `IFileShares`, `IFiles`, `IFlowStarter`, `IFlows`, `IFocus`, `IFriends`, `IGroupChat`, `IImageGeneration`, `IImageGenerator`, `IInoConversations`, `IInoRecords`, `IInstalledApps`, `IInstalledModules`, `IIntentUsage`, `IKnowledgeStore`, `ILLM`, `ILegacyPreferenceField`, `ILiveEnvironment`, `ILiveVerifier`, `IMarketplace`, `IMcpConnectionSeedProvider`, `IMcpToolConnection`, `IMemory`, `IModelCatalog`, `IModelInference`, `INotifications`, `IPolicyModel`, `IPosts`, `IPresence`, `IProfileContextWriter`, `IProfile`, `IRoom`, `IRooms`, `IRoslyn`, `IRouter`, `IRuns`, `ISandboxRuntimeBridge`, `IScreenDirectory`, `IScreenTargetResolver`, `ISecurityPosture`, `ISelfImprovementLoop`, `ISessionJournal`, `ISessionList`, `ISharedWithMe`, `IStudio`, `ISynapseDefinitions`, `ISynapseStudio`, `ISystemOne`, `ITestVerificationNeuron`, `ITestVerificationService`, `IToneObserver`, `IToolSource`, `ITraceReader`, `IUiIntrospector`, `IUiRootProvider`, `IUnitVerifier`, `IUserSettings`, `IVectorIndex`, `IVennSystemOne`, `IView`, `IViewport`, `IWebBrowser`, `IWorkflowRun`, `IWorkingMemory`, `IWorkspaceFiles`, `IWorkspace`, `IWorktreeRoslyn`).
Hosting: runs in process.
<!-- END GENERATED -->
