using Orleans.Runtime;

namespace IntoChat.OS.Modules.AI.Models.OpenAI;

[GrainType("ai.llm.gpt54")]
internal sealed class Gpt54Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenAI.IGpt54)), IntoChat.OS.Modules.AI.Models.OpenAI.IGpt54;

[GrainType("ai.llm.gpt54mini")]
internal sealed class Gpt54MiniNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenAI.IGpt54Mini)), IntoChat.OS.Modules.AI.Models.OpenAI.IGpt54Mini;

[GrainType("ai.llm.gpt54nano")]
internal sealed class Gpt54NanoNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenAI.IGpt54Nano)), IntoChat.OS.Modules.AI.Models.OpenAI.IGpt54Nano;

[GrainType("ai.llm.gpt56luna")]
internal sealed class Gpt56LunaNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenAI.IGpt56Luna)), IntoChat.OS.Modules.AI.Models.OpenAI.IGpt56Luna;

[GrainType("ai.llm.gpt56sol")]
internal sealed class Gpt56SolNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenAI.IGpt56Sol)), IntoChat.OS.Modules.AI.Models.OpenAI.IGpt56Sol;

[GrainType("ai.llm.gpt56terra")]
internal sealed class Gpt56TerraNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenAI.IGpt56Terra)), IntoChat.OS.Modules.AI.Models.OpenAI.IGpt56Terra;
