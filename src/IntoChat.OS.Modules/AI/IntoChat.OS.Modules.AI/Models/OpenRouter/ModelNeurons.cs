using Orleans.Runtime;

namespace IntoChat.OS.AI.Models.OpenRouter;

[GrainType("ai.llm.deepseek-v41-flash")]
internal sealed class DeepSeekV41FlashNeuron(InferenceService inference)
    : LlmNeuronBase(inference, typeof(IntoChat.OS.AI.Models.OpenRouter.IDeepSeekV41Flash)), IntoChat.OS.AI.Models.OpenRouter.IDeepSeekV41Flash;
