using Orleans.Runtime;

namespace DigitalBrain.AI.Models.XAI;

[GrainType("ai.llm.grok46")]
internal sealed class Grok46Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.XAI.IGrok46)), DigitalBrain.AI.Models.XAI.IGrok46;
