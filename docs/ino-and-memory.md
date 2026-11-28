# Ino and memory

Ino is the agent that does work for you. It is a system app, `InoApp`, in `src/IntoChat.OS.Apps/Ino`. The agent itself, its contexts and its memory come from the AI module in `src/Modules/AI`.

## The app and the conversation

`InoApp` is the chat window. It does not call a model and stores no copy of the transcript. Each conversation is a `ConversationNeuron`, addressed through the `IConversation` contract under the key `InoModel.ConversationKeyOf` (`conversations/{id}`), with `IThreadDirectory` (`ThreadDirectoryNeuron`) tracking the list of threads and the active conversation.

`InoAgentDefinition` builds the `AgentDefinition` that the app seats on the conversation (`IConversation.Seat`). It holds three things:

- **Instructions.** The persona, with your display name filled in from settings.
- **Contracts.** The neuron contracts the model may act on, for example `IStudio`, `IData`, `IFocus`, `ISynapseStudio`, `IMemory`, `IFlows` and `ICodeGraph`.
- **Contexts.** The context neurons the agent reads at the start of every turn.

Models are catalogued as `ModelCard` entries in `KnownModels` and routed by `ModelRole` (`Chat`, `Think`, `Fast`, `Hear`, `Speak`, `Talk`, `Embed`, `Image`) through `IModelCatalog` (`List`, `Pick`, `SetRole`, `Roles`, `Availability`) and one `ILLM` neuron per model (`{brain}/llm/{modelId}`). Choosing a model in the composer switches that conversation; pinning it or choosing a role in Settings → AI updates the person's role override read by `ModelCatalog.Decide` ahead of the deployment `RoleMap` defaults.

## A turn

1. You send a message. `InoApp` receives it as an `InoSend` signal.
2. A message that starts with `/` is handled as a command. If the compute budget is exceeded, the turn stops there.
3. `InoApp` ensures Ino is seated on the conversation and calls `IConversation.Say`. The conversation appends the person's `Said` entry and starts a run at once.
4. `ConversationNeuron` runs the turn through `TurnRunner` and `AgentTurnLoop`, appending `Called`, `Returned`, optional `Summarized`, and the reply `Said` entry to the conversation's own log.
5. `ConversationNeuron` publishes `ConversationChanged`. `InoApp` reads `ConversationView` and projects the person view through `InoThread`, and the shell draws the updated view.

`InoStop` calls `IConversation.Stop` to cancel the active run while preserving partial text; retrying a failed or cancelled bubble calls `IConversation.Regenerate`. `InoNew` opens a fresh conversation id in `IThreadDirectory`, and `InoShowChats` toggles the sessions list.

## Tools

Each method on a contract in the agent definition is exposed directly as a typed tool (`ContractTool`, generated from `ToolSchema`) taking named JSON arguments; `ToolRouter` picks the top-K tools for each turn alongside `neurons_search`, `neurons_list`, and `neurons_context` (`AgentTools`). When the model calls `neurons_search`, matching allowed contracts are added to the turn's tools for the next step. All calls bind through `NeuronBinder` on `NeuronSurface`, the same surface MCP and gRPC use. See [architecture](architecture.md).

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
| `ui.focus`, `ui.state` | What you are looking at. `IFocus` writes the focus when Ino points you at something. |
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

## Voice

Ino hears, speaks and holds live voice conversations through Microsoft.Extensions.AI's speech and realtime interfaces (`ISpeechToTextClient`, `ITextToSpeechClient` and `IRealtimeClient`), routed by `ModelRole.Hear`, `ModelRole.Speak` and `ModelRole.Talk` via `IModelCatalog` and `VoiceClients`:

- **Dictation (`POST /ai/transcriptions`).** Uploads audio (including OGG/Opus converted through `IAudioConverter`) to the picked `ModelRole.Hear` model — locally through `FoundryLocalRuntime` (`KnownModels.WhisperLargeV3Turbo`) or in the cloud through OpenAI Audio (`KnownModels.Gpt4oTranscribe`).
- **Read-aloud (`POST /ai/speech`).** Synthesizes a reply through `SpeechEndpoint` and the picked `ModelRole.Speak` model — locally through `SherpaOnnxRuntime` (`KnownModels.Kokoro82M`, 24 kHz PCM wrapped as WAV) or in the cloud through OpenAI TTS (`KnownModels.Gpt4oMiniTts`).
- **Voice mode (`GET /ai/talk?conversation={id}`).** Opens a full-duplex WebSocket handled by `TalkEndpoint` carrying 16 kHz PCM16 LE mono input, 24 kHz PCM16 LE mono output and `TalkJson` control frames (`TalkEvent`). On the device, an unbound `ModelRole.Talk` runs `LocalTalk` (Silero `IVoiceActivityDetector` → Hear → `ConversationChatClient` over `IConversation.Say` and `TextDelta` → Speak) with barge-in that cancels the active conversation run via `IConversation.Stop`. When `ModelRole.Talk` is bound to a cloud card (`KnownModels.GptRealtime`), `VoiceClients` opens an `OpenAIRealtimeClient` and `TalkEndpoint` records each completed exchange once via `IConversation.Voiced` (`EntryIds.Heard` and `EntryIds.Spoken`).

`InoModelSetup.ReadVoiceAsync` checks `IModelCatalog.Availability` for the voice roles and `InoView.ThreadProps` exposes the result on `ChatThreadProps` (`Dictation`, `ReadAloud` and `TalkConversation`).
