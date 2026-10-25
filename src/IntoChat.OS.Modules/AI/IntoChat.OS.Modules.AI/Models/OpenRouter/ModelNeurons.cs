using Orleans.Runtime;

namespace IntoChat.OS.Modules.AI.Models.OpenRouter;

[GrainType("ai.llm.deepseek-v41-flash")]
internal sealed class DeepSeekV41FlashNeuron(InferenceService inference)
    : LlmNeuronBase(inference, typeof(IntoChat.OS.Modules.AI.Models.OpenRouter.IDeepSeekV41Flash)), IntoChat.OS.Modules.AI.Models.OpenRouter.IDeepSeekV41Flash;
