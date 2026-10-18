using Orleans.Runtime;

namespace IntoChat.OS.AI.Models.Ollama;

[GrainType("ai.llm.gemma4")]
internal sealed class Gemma4Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.AI.Models.Ollama.IGemma4)), IntoChat.OS.AI.Models.Ollama.IGemma4;

[GrainType("ai.llm.qwen35")]
internal sealed class Qwen35Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.AI.Models.Ollama.IQwen35)), IntoChat.OS.AI.Models.Ollama.IQwen35;
