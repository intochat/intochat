using Orleans.Runtime;

namespace IntoChat.OS.Modules.AI.Models.Anthropic;

[GrainType("ai.llm.fable5")]
internal sealed class Fable5Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IFable5)), IntoChat.OS.Modules.AI.Models.Anthropic.IFable5;

[GrainType("ai.llm.haiku45")]
internal sealed class Haiku45Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IHaiku45)), IntoChat.OS.Modules.AI.Models.Anthropic.IHaiku45;

[GrainType("ai.llm.opus5")]
internal sealed class Opus5Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IOpus5)), IntoChat.OS.Modules.AI.Models.Anthropic.IOpus5;

[GrainType("ai.llm.sonnet5")]
internal sealed class Sonnet5Neuron(InferenceService inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.ISonnet5)), IntoChat.OS.Modules.AI.Models.Anthropic.ISonnet5;
