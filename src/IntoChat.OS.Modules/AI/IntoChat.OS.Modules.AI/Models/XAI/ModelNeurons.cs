using Orleans.Runtime;

namespace IntoChat.OS.AI.Models.XAI;

[GrainType("ai.llm.grok46")]
internal sealed class Grok46Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.AI.Models.XAI.IGrok46)), IntoChat.OS.AI.Models.XAI.IGrok46;
