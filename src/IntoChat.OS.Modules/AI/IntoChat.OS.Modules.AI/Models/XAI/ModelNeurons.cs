using Orleans.Runtime;

namespace IntoChat.OS.Modules.AI.Models.XAI;

[GrainType("ai.llm.grok46")]
internal sealed class Grok46Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.XAI.IGrok46)), IntoChat.OS.Modules.AI.Models.XAI.IGrok46;
