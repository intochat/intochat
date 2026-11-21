# Ino and memory

Ino is the agent that does work for you. It is a system app, `InoApp`, in `src/IntoChat.OS.Apps/Ino`. The agent itself, its contexts and its memory come from the AI module in `src/Modules/AI`.

## The app and the agent

`InoApp` is the chat window. It does not call a model. Each conversation has its own agent neuron, addressed through the `IAgent` contract. The key is built from the conversation id by `InoModel.AgentKeyOf`.

`InoAgentDefinition` builds the `AgentDefinition` that the app gives the agent. It holds three things:

- **Instructions.** The persona, with your display name filled in from settings.
- **Contracts.** The neuron contracts the model may act on, for example `IInoStudio`, `IInoData`, `IInoFocus`, `ISynapseStudio`, `IMemory`, `IFlows` and `ICodeGraph`.
- **Contexts.** The context neurons the agent reads at the start of every turn.

Models are catalogued as `ModelCard` entries in `KnownModels` and routed by `Role` (`Chat`, `Think`, `Fast`, `Hear`, `Speak`, `Talk`, `Embed`, `Image`) through `IModelCatalog` (`List`, `Pick`, `SetRole`, `Roles`, `Availability`) and one `ILLM` neuron per model (`{brain}/llm/{modelId}`). Choosing a model in the composer switches that conversation; pinning it or choosing a role in Settings → AI updates the person's role override read by `ModelCatalog.Decide` ahead of the deployment `RoleMap` defaults.

## A turn

1. You send a message. `InoApp` receives it as an `InoSend` signal.
2. A message that starts with `/` is handled as a command. If the compute budget is exceeded, the turn stops there.
3. `InoApp` configures the agent and calls `IAgent.Start`. The call returns a run id at once.
4. The agent runs the turn in `AgentTurnLoop`. The agent keeps the transcript.
5. The agent reports the turn as signals. `InoApp` follows `ChatChanged` and `ChatRunChanged` and projects the chat into its state. The shell draws the new view.

`InoStop` cancels the active run. `InoNew` starts a new conversation with a new agent key.

## Tools

The model has five tools: `neurons_list`, `neurons_search`, `neurons_get`, `neurons_call` and `neurons_context`. It reaches every neuron method through `neurons_call`. The tools sit on `NeuronSurface`, the same surface the shell and MCP use. See [architecture](architecture.md).

The tools come only from the contracts in the agent definition. A context entry never adds a tool.

## Context

A context is a neuron with the `IAgentContext` contract. It holds named text entries. Each entry records who wrote it and with what trust.

Ino lists nine contexts, each under the key `context/` plus a name:

| Name | What it holds |
| --- | --- |
| `user.profile` | Who you are: name, language, time zone, date format. Written from your settings. |
| `conversation.summary` | A summary of earlier conversation, written by the agent when the transcript is shortened. |
| `conversation.inference`, `conversation.inference.tone` | What has been inferred about the conversation. `ToneObserverNeuron` writes the tone. |
| `apps.capabilities` | What the apps in the brain can do. |
| `security.posture` | The brain's security posture, written by `SecurityPostureNeuron`. |
| `ui.focus`, `ui.state` | What you are looking at. `IInoFocus` writes the focus when Ino points you at something. |
| `memory.recall` | What memory recalled for this turn. |

Before each turn the agent reads every listed context. A context that is missing or fails is skipped; the turn still runs. Untagged entries reach the model as knowledge.

An entry tagged as an interrupt stops the turn before it starts, but only when you or the platform wrote it. An interrupt written by an app or by the model is stored and not acted on.

## Memory

Memory is one neuron per brain with the `IMemory` contract, implemented by `MemoryNeuron`. It keeps four kinds of things in its own state, with no external database:

- **Episodes**: short summaries of something that happened.
- **Facts**: durable facts about you or the brain.
- **Notes**: preferences, conventions and corrections, each with a scope such as `owner`, `global` or `repo:<name>`.
- **Edges**: links between entities.

**Writing.** Ino records a preference by calling `RememberPreference` when you state one. `InoApp` records an episode after a database query and after a make request. A note written through a model's tool call has `Agent` trust. A note you write directly has `Owner` trust, and `PromoteNote` raises a note to `Owner` trust.

**Recall.** Before a turn is planned, the agent calls `RecallPack` with your message. The result has two parts:

- Owner rules: notes with `Owner` trust whose scope matches. These are selected by scope, not by score.
- Recalled items: the best matches among notes, facts and episodes. The score combines word match, embedding similarity and links in the entity graph.

The agent writes both into the `memory.recall` context, so the turn reads them like any other context. If recall fails, the turn runs without it.

Embeddings come from a local Ollama endpoint when one answers in time. Otherwise `HybridMemoryEmbedder` computes a deterministic local embedding, so recall works with no model service.
