using DigitalBrain;
using DigitalBrain.Neurons;


namespace IntoChat.OS.Modules.AI.Models.Anthropic;

[Neuron("ai.llm.fable5")]
internal sealed class Fable5Neuron(IModelInference inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IFable5)), IntoChat.OS.Modules.AI.Models.Anthropic.IFable5;

[Neuron("ai.llm.haiku45")]
internal sealed class Haiku45Neuron(IModelInference inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IHaiku45)), IntoChat.OS.Modules.AI.Models.Anthropic.IHaiku45;

[Neuron("ai.llm.opus46")]
internal sealed class Opus46Neuron(IModelInference inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IOpus46)), IntoChat.OS.Modules.AI.Models.Anthropic.IOpus46;

[Neuron("ai.llm.opus5")]
internal sealed class Opus5Neuron(IModelInference inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.IOpus5)), IntoChat.OS.Modules.AI.Models.Anthropic.IOpus5;

[Neuron("ai.llm.sonnet46")]
internal sealed class Sonnet46Neuron(IModelInference inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.ISonnet46)), IntoChat.OS.Modules.AI.Models.Anthropic.ISonnet46;

[Neuron("ai.llm.sonnet5")]
internal sealed class Sonnet5Neuron(IModelInference inference) : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.Anthropic.ISonnet5)), IntoChat.OS.Modules.AI.Models.Anthropic.ISonnet5;
