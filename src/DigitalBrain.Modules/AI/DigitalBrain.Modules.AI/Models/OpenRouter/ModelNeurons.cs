using Orleans.Runtime;

namespace DigitalBrain.AI.Models.OpenRouter;

[GrainType("ai.llm.deepseek-v41-flash")]
internal sealed class DeepSeekV41FlashNeuron(InferenceService inference)
    : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.OpenRouter.IDeepSeekV41Flash)), DigitalBrain.AI.Models.OpenRouter.IDeepSeekV41Flash;
