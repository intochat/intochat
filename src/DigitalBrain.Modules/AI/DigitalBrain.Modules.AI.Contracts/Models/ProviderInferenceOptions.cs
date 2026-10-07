using System.Text.Json.Serialization;

namespace DigitalBrain.AI.Models;

[GenerateSerializer, Alias("ai.provider-inference-options")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$provider")]
[JsonDerivedType(typeof(OpenAI.OpenAIInferenceOptions), "openai")]
[JsonDerivedType(typeof(Ollama.OllamaInferenceOptions), "ollama")]
public abstract record ProviderInferenceOptions;
