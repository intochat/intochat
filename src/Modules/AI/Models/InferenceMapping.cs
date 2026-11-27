using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IntoChat.OS.Modules.AI.Inference;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Models;

internal static class InferenceMapping
{
    // Reasoning and MaxOutputTokens belong to the model selection; the options handed on never carry Reasoning.
    // Later overrides win per field. Persona is not an inference setting and is read by the caller.
    public static (ModelSelection? Selection, InferenceOptions? Settings) Merge(
        ModelSelection? baseSelection, InferenceOptions? baseOptions, params InferenceOverride?[] overrides)
    {
        var applied = (overrides ?? []).OfType<InferenceOverride>().ToArray();
        var model = applied.Select(o => o.Model).LastOrDefault(v => v is not null);
        var temperature = applied.Select(o => o.Temperature).LastOrDefault(v => v is not null) ?? baseOptions?.Temperature;
        var topP = applied.Select(o => o.TopP).LastOrDefault(v => v is not null) ?? baseOptions?.TopP;
        var reasoning = applied.Select(o => o.Reasoning).LastOrDefault(v => v is not null) ?? baseOptions?.Reasoning;
        var maximum = applied.Select(o => o.MaxOutputTokens).LastOrDefault(v => v is not null) ?? baseOptions?.MaxOutputTokens;

        var selection = baseSelection;
        if (model is not null || reasoning is not null || maximum is not null)
        {
            selection = (selection ?? new()) with
            {
                Model = model ?? selection?.Model,
                Reasoning = reasoning ?? selection?.Reasoning,
                MaxOutputTokens = maximum ?? selection?.MaxOutputTokens,
            };
        }
        var overridden = applied.Any(o => o.Temperature is not null || o.TopP is not null || o.MaxOutputTokens is not null);
        var settings = baseOptions is null && !overridden
            ? null
            : (baseOptions ?? new()) with { Temperature = temperature, TopP = topP, MaxOutputTokens = maximum, Reasoning = null };
        return (selection, settings);
    }

    internal static ChatMessage ToChatMessage(AiMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role is not ("system" or "developer" or "user" or "assistant" or "tool"))
        { throw new ArgumentException($"Unknown message role '{message.Role}'.", nameof(message)); }
        var chatMessage = new ChatMessage(new ChatRole(message.Role), message.Content.Select(ToContent).ToList());
        if (message.Sender is { } sender)
        {
            chatMessage.AuthorName = !string.IsNullOrWhiteSpace(sender.DisplayName) ? sender.DisplayName : sender.AccountId;
        }
        return chatMessage;
    }

    internal static AiMessage FromChatMessage(ChatMessage message)
        => new(message.Role.Value, message.Contents.Where(c => c is not UsageContent).Select(FromContent).ToArray());

    internal static AIContent ToContent(AiContent content) => content switch
    {
        AiText text => new TextContent(text.Text),
        AiReasoning reasoning => new TextReasoningContent(reasoning.Text),
        AiImage image => Media(image.Uri, image.MediaType, "image/"),
        AiAudio audio => Media(audio.Uri, audio.MediaType, "audio/"),
        AiToolCall call => new FunctionCallContent(call.CallId, call.Name,
            (Dictionary<string, object?>?)JsonSerializer.Deserialize(call.ArgumentsJson, ToolJson.Type<Dictionary<string, object?>>())
                ?? throw new ArgumentException("Tool arguments must be a JSON object.")),
        AiToolResult result => new FunctionResultContent(result.CallId, ParseToolResultJsonOrText(result.ResultJson)),
        _ => throw new NotSupportedException($"Unsupported inference content {content.GetType().Name}."),
    };

    private static JsonElement ParseToolResultJsonOrText(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return JsonSerializer.SerializeToElement(string.Empty);
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw);
        }
    }

    internal static AiContent FromContent(AIContent content) => content switch
    {
        TextContent text => new AiText(text.Text),
        TextReasoningContent reasoning => new AiReasoning(reasoning.Text ?? string.Empty),
        UriContent uri when uri.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => new AiImage(uri.Uri.ToString(), uri.MediaType),
        UriContent uri when uri.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => new AiAudio(uri.Uri.ToString(), uri.MediaType),
        DataContent data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => new AiImage(data.Uri, data.MediaType),
        DataContent data when data.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => new AiAudio(data.Uri, data.MediaType),
        FunctionCallContent call => new AiToolCall(call.CallId, call.Name, ToolJson.Serialize(call.Arguments)),
        FunctionResultContent result => new AiToolResult(result.CallId, ToolJson.Serialize(result.Result)),
        _ => throw new NotSupportedException($"Unsupported provider content {content.GetType().Name}; it cannot be silently dropped."),
    };

    private static AIContent Media(string uri, string? mediaType, string prefix)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            const int maximumBytes = 8 * 1024 * 1024;
            var comma = uri.IndexOf(',');
            if (comma is < 1 or > 256 || uri.Length - comma - 1 > (maximumBytes + 2) / 3 * 4)
            { throw new ArgumentException("Inline media exceeds 8 MiB or has an invalid data URI header."); }
            var data = new DataContent(uri);
            if (data.Data.Length is 0 or > maximumBytes) { throw new ArgumentException("Inline media must contain between 1 byte and 8 MiB."); }
            if (!data.MediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Content media type does not match its modality."); }
            return data;
        }
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
            || mediaType is null || !mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Media requires a data URI or an absolute HTTP(S) URI and matching media type."); }
        return new UriContent(parsed, mediaType);
    }

    internal static void ValidateRequest(InferenceRequest request, ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (request.Messages is not { Count: > 0 }) { throw new ArgumentException("At least one message is required.", nameof(request)); }
        if (request.Messages.SelectMany(m => m.Content).Any(c => c is AiImage) && descriptor.Vision != CapabilitySupport.Supported)
        { throw new ArgumentException("The model does not declare vision support.", nameof(request)); }
        if (request.Messages.SelectMany(m => m.Content).Any(c => c is AiAudio))
        { throw new ArgumentException("These chat adapters do not declare audio input support; use transcription first.", nameof(request)); }
        if (request.Tools is { Count: > 0 } && descriptor.Tools != CapabilitySupport.Supported)
        { throw new ArgumentException("The model does not declare tool support.", nameof(request)); }
        ValidateOptions(request.Options, descriptor);
    }

    private static void ValidateOptions(InferenceOptions? options, ModelDescriptor? descriptor)
    {
        if (options?.Temperature is not null && descriptor?.Temperature != CapabilitySupport.Supported)
        { throw new ArgumentException("Temperature support must be declared by the selected model profile.", nameof(options)); }
        if (options?.TopP is not null && descriptor?.TopP != CapabilitySupport.Supported)
        { throw new ArgumentException("TopP support must be declared by the selected model profile.", nameof(options)); }
        if (options?.Reasoning is not null && descriptor?.AllowedReasoning?.Contains(options.Reasoning, StringComparer.OrdinalIgnoreCase) != true)
        { throw new ArgumentException("The selected model profile must declare this reasoning value in AllowedReasoning.", nameof(options)); }
        if (options?.MaxOutputTokens is { } maximum && descriptor?.MaximumOutputTokens is { } limit && maximum > limit)
        { throw new ArgumentException($"MaxOutputTokens exceeds the model's declared output limit {limit}.", nameof(options)); }
        if (options?.ResponseSchemaJson is not null && descriptor?.StructuredOutput != CapabilitySupport.Supported)
        { throw new ArgumentException("The model does not declare structured output support.", nameof(options)); }
    }

    internal static ChatOptions CreateOptions(ResolvedModel model, InferenceOptions? settings, ModelDescriptor? descriptor = null)
    {
        settings ??= new();
        ValidateOptions(settings, descriptor);
        if (model.Reasoning is not null && descriptor?.AllowedReasoning is { } allowed
            && !allowed.Contains(model.Reasoning, StringComparer.OrdinalIgnoreCase))
        { throw new ArgumentException("The resolved reasoning value is not allowed by the model profile.", nameof(model)); }
        if (model.MaxOutputTokens is { } configuredMaximum && descriptor?.MaximumOutputTokens is { } modelMaximum && configuredMaximum > modelMaximum)
        { throw new ArgumentException("The resolved maximum output tokens exceed the model's declared output limit.", nameof(model)); }
        if (settings.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature < 0 || temperature > 2))
        { throw new ArgumentOutOfRangeException(nameof(settings), "Temperature must be finite and between 0 and 2."); }
        if (settings.TopP is { } topP && (!float.IsFinite(topP) || topP < 0 || topP > 1))
        { throw new ArgumentOutOfRangeException(nameof(settings), "TopP must be finite and between 0 and 1."); }
        if (settings.MaxOutputTokens is <= 0) { throw new ArgumentOutOfRangeException(nameof(settings), "MaxOutputTokens must be positive."); }
        var options = ModelCatalog.CreateOptions(model);
        if (model.Provider == AiProvider.Ollama && model.Reasoning is { } effort)
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties["think"] = effort switch
            {
                "none" => false,
                "low" or "medium" or "high" => effort,
                _ => throw new ArgumentException("Ollama supports reasoning none, low, medium or high through this adapter.", nameof(model)),
            };
        }
        if (settings.ResponseSchemaJson is not null)
        { options.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.Deserialize<JsonElement>(settings.ResponseSchemaJson)); }
        if (settings.Provider is not null && settings.ProviderOptions is { Count: > 0 })
        { throw new ArgumentException("Select typed provider settings or extension properties, not both.", nameof(settings)); }
        switch (settings.Provider)
        {
            case null: break;
            case OpenAI.OpenAIInferenceOptions openai when model.Provider == AiProvider.OpenAI:
                options.AllowMultipleToolCalls = openai.ParallelToolCalls;
                break;
            case Ollama.OllamaInferenceOptions ollama when model.Provider == AiProvider.Ollama:
                options.AdditionalProperties ??= [];
                if (ollama.ContextWindowTokens is { } context)
                {
                    if (context <= 0 || descriptor?.ContextWindowTokens is not { } maximum || context > maximum)
                    { throw new ArgumentException("Ollama context window must be positive and within a declared model context limit.", nameof(settings)); }
                    options.AdditionalProperties["num_ctx"] = context;
                }
                if (ollama.Think is { } think)
                {
                    if (model.Reasoning is not null) { throw new ArgumentException("Select Ollama Think or reasoning effort, not both.", nameof(settings)); }
                    options.AdditionalProperties["think"] = think;
                }
                break;
            default: throw new ArgumentException("The provider settings do not match the selected provider.", nameof(settings));
        }
        // Only explicitly mapped extensions are accepted; arbitrary SDK objects never cross the contract.
        foreach (var option in settings.ProviderOptions ?? new Dictionary<string, string>())
        {
            options.AllowMultipleToolCalls = model.Provider == AiProvider.OpenAI && option.Key == "parallel_tool_calls" && bool.TryParse(option.Value, out var parallel)
                ? parallel : throw new ArgumentException($"Unsupported {model.Provider} option '{option.Key}' or invalid value.", nameof(settings));
        }
        options.Temperature = settings.Temperature;
        options.TopP = settings.TopP;
        options.MaxOutputTokens = settings.MaxOutputTokens ?? options.MaxOutputTokens;
        if (settings.Reasoning is not null)
        { throw new ArgumentException("Select reasoning through InferenceRequest.Model.Reasoning so provider validation applies.", nameof(settings)); }
        return options;
    }

    internal static InferenceUsage? Usage(UsageDetails? usage)
        => usage is null ? null : new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount);
}

// A per-turn override read from a context entry; every field is optional and only set fields apply.
internal sealed record InferenceOverride(string? Model, float? Temperature, float? TopP, string? Reasoning, int? MaxOutputTokens, string? Persona)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static InferenceOverride? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<InferenceOverride>(json, Json); }
        catch (JsonException) { return null; }
    }
}

// One serializer for tool arguments, results and usage: MEAI's source-generated contracts, so no reflection path
// (AOT/trim safe). MEAI's own options write indented JSON; tool strings stay compact. Values go through the object
// contract, which resolves the runtime type against the same options and writes null as "null".
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = Compact();

    public static JsonTypeInfo Type<T>() => Options.GetTypeInfo(typeof(T));

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Type<object>());

    private static JsonSerializerOptions Compact()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.MakeReadOnly();
        return options;
    }
}
