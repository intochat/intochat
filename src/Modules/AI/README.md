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

Contracts: assembly `IntoChat.OS.Modules.AI.Contracts` (`IAgentContext`, `IAgent`, `IEmbeddingModel`, `IGroupChat`, `IImageGeneration`, `IImageGenerator`, `IIntentUsage`, `IKnowledgeStore`, `ILLM`, `IMemory`, `IModelCatalog`, `IModelInference`, `IRouter`, `ISecurityPosture`, `ISessionJournal`, `ISystemOne`, `ITestVerificationNeuron`, `ITestVerificationService`, `IToneObserver`, `IUiIntrospector`, `IVectorIndex`, `IWorkflowRun`, `IWorkingMemory`).
Hosting: runs in process.
<!-- END GENERATED -->
