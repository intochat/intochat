namespace IntoChat.OS.Modules.AI.Models;

public abstract class LLMModel : AiModel
{
    // Defaults to tool-calling only. Every catalogued model has always been treated as tool-capable;
    // vision and structured output go undeclared until a model is actually confirmed to support them.
    public virtual LlmCapabilities Capabilities => LlmCapabilities.Tools;

    public bool SupportsTools => Capabilities.HasFlag(LlmCapabilities.Tools);

    // Cloud models precede local ones: when no default is configured, the first
    // model whose provider has credentials becomes the default chat model.
    public static IReadOnlyList<LLMModel> All { get; } =
    [
        new OpenAI.Gpt56Sol(),
        new OpenAI.Gpt56Terra(),
        new OpenAI.Gpt56Luna(),
        new OpenAI.Gpt54(),
        new OpenAI.Gpt54Mini(),
        new OpenAI.Gpt54Nano(),
        new Anthropic.Fable5(),
        new Anthropic.Opus5(),
        new Anthropic.Sonnet5(),
        new Anthropic.Haiku45(),
        new Google.Gemini31Pro(),
        new Google.Gemini36Pro(),
        new Google.Gemini36Flash(),
        new XAI.Grok46(),
        new OpenRouter.DeepSeekV41Flash(),
        new Ollama.Gemma4(),
        new Ollama.Qwen35(),
    ];

    public static LLMModel? FindByMarker(Type marker)
        => All.FirstOrDefault(model => model.Marker == marker);

    public static LLMModel? FindByMarkerName(string markerName)
        => All.FirstOrDefault(model => string.Equals(model.Marker.Name, markerName, StringComparison.Ordinal));

    public static LLMModel? FindById(string id)
        => All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));

    // Resolves a catalogued model by its marker name (with or without leading 'I', e.g. "IQwen35" or "Qwen35"),
    // choice prefix ("preset:IQwen35"), or model ID (e.g. "qwen3.5:4b", "claude-sonnet-5", case-insensitive).
    public static LLMModel? Find(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) { return null; }
        var key = nameOrId.Trim();
        if (key.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
        {
            key = key["preset:".Length..].Trim();
        }
        return FindByMarkerName(key)
            ?? FindById(key)
            ?? All.FirstOrDefault(model =>
                string.Equals(model.Marker.Name, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.Marker.Name, "I" + key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.Id, key, StringComparison.OrdinalIgnoreCase));
    }
}

public abstract class LLMModel<TMarker> : LLMModel
    where TMarker : ILLM
{
    public sealed override Type Marker => typeof(TMarker);
}
