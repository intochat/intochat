using Orleans.Runtime;

namespace DigitalBrain.AI.Models.Anthropic;

[GrainType("ai.llm.fable5")]
internal sealed class Fable5Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Anthropic.IFable5)), DigitalBrain.AI.Models.Anthropic.IFable5;

[GrainType("ai.llm.haiku45")]
internal sealed class Haiku45Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Anthropic.IHaiku45)), DigitalBrain.AI.Models.Anthropic.IHaiku45;

[GrainType("ai.llm.opus5")]
internal sealed class Opus5Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Anthropic.IOpus5)), DigitalBrain.AI.Models.Anthropic.IOpus5;

[GrainType("ai.llm.sonnet5")]
internal sealed class Sonnet5Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Anthropic.ISonnet5)), DigitalBrain.AI.Models.Anthropic.ISonnet5;
