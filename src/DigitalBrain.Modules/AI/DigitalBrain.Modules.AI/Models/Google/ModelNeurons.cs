using Orleans.Runtime;

namespace DigitalBrain.AI.Models.Google;

[GrainType("ai.llm.gemini31pro")]
internal sealed class Gemini31ProNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Google.IGemini31Pro)), DigitalBrain.AI.Models.Google.IGemini31Pro;

[GrainType("ai.llm.gemini36flash")]
internal sealed class Gemini36FlashNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Google.IGemini36Flash)), DigitalBrain.AI.Models.Google.IGemini36Flash;

[GrainType("ai.llm.gemini36pro")]
internal sealed class Gemini36ProNeuron(InferenceService inference) : LlmNeuronBase(inference, typeof(DigitalBrain.AI.Models.Google.IGemini36Pro)), DigitalBrain.AI.Models.Google.IGemini36Pro;
