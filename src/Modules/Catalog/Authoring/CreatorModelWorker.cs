using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

internal static class CreatorModel
{
    public const string AutoModel = "auto";
    private const string DefaultOllamaEndpoint = "http://127.0.0.1:11434";

    private static bool IsRecoverableProviderError(Exception ex) => ex is InvalidOperationException
        or ArgumentException
        or KeyNotFoundException
        or NotSupportedException
        or HttpRequestException
        or TimeoutException
        or IntoChat.OS.Modules.AI.Models.ProviderUnavailableException;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<string> Complete(IServiceProvider neurons, string brainId, string? model, string system, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var request = new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(prompt)]),
        ]);
        var result = await InvokeAsync(neurons, brainId, model, request, requiresTools: false, cancellationToken).ConfigureAwait(false);
        return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
    }

    public static Task<InferenceResult> Generate(IServiceProvider neurons, string brainId, string? model, InferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(neurons, brainId, model, request, requiresTools: request.Tools is { Count: > 0 }, cancellationToken);
    }

    private static async Task<InferenceResult> InvokeAsync(
        IServiceProvider neurons,
        string brainId,
        string? model,
        InferenceRequest request,
        bool requiresTools,
        CancellationToken ct)
    {
        var explicitModel = string.IsNullOrWhiteSpace(model) || string.Equals(model.Trim(), AutoModel, StringComparison.OrdinalIgnoreCase)
            ? null
            : model.Trim();

        // 1. Resolve through ModelCatalog + IModelInference when registered in DI (respects brain keys, Anthropic/OpenAI/Gemini/Ollama, and Host IChatClient)
        var catalog = neurons.GetService<ModelCatalog>();
        var inference = neurons.GetService<IModelInference>();
        if (catalog is not null)
        {
            var resolved = await ResolveCatalogModelAsync(neurons, catalog, brainId, explicitModel, requiresTools, ct).ConfigureAwait(false);
            if (resolved is not null)
            {
                if (inference is not null)
                {
                    var selection = new ModelSelection(
                        resolved.Provider == AiProvider.Host ? null
                        : resolved.Profile is { Length: > 0 } ? $"profile:{resolved.Profile}"
                        : resolved.Provider == AiProvider.Ollama ? $"ollama/{resolved.Model}"
                        : resolved.Model,
                        resolved.Reasoning,
                        resolved.MaxOutputTokens);
                    try
                    {
                        return await inference.Generate(request with { Model = selection }, marker: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                    }
                    catch (Exception error) when (explicitModel is not null && IsRecoverableProviderError(error))
                    {
                        CreatorLogger(neurons).LogSwallowed(error, "Explicit Creator model selection failed; retrying automatic brain default.");
                        var autoResolved = await ResolveCatalogModelAsync(neurons, catalog, brainId, explicitModel: null, requiresTools, ct).ConfigureAwait(false);
                        if (autoResolved is not null)
                        {
                            var autoSelection = new ModelSelection(
                                autoResolved.Provider == AiProvider.Host ? null
                                : autoResolved.Profile is { Length: > 0 } ? $"profile:{autoResolved.Profile}"
                                : autoResolved.Provider == AiProvider.Ollama ? $"ollama/{autoResolved.Model}"
                                : autoResolved.Model,
                                autoResolved.Reasoning,
                                autoResolved.MaxOutputTokens);
                            return await inference.Generate(request with { Model = autoSelection }, marker: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                        }
                    }
                }

                // Direct ILLM neuron fallback when ModelCatalog resolved a compiled preset and IModelInference is not registered
                if (LLMModel.Find(resolved.Model) is { } preset)
                {
                    return await ((ILLM)SiloNeurons.Get(neurons, preset.Marker, ModelAddress.Key(brainId))).Generate(request, ct).ConfigureAwait(false);
                }
            }
        }

        if (inference is not null)
        {
            var selection = explicitModel is null ? null : new ModelSelection(explicitModel);
            try
            {
                var desc = await inference.Describe(selection, marker: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                if ((!requiresTools || desc.Tools == CapabilitySupport.Supported)
                    && (!string.Equals(desc.Provider, "Host", StringComparison.OrdinalIgnoreCase)
                        || neurons.GetService<global::Microsoft.Extensions.AI.IChatClient>() is not null))
                {
                    return await inference.Generate(request with { Model = selection }, marker: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException && IsRecoverableProviderError(error))
            {
                CreatorLogger(neurons).LogSwallowed(error, "IModelInference unavailable; probing local Ollama fallback.");
            }
        }

        if (explicitModel is not null && LLMModel.Find(explicitModel) is { } compiledMarker)
        {
            try
            {
                return await ((ILLM)SiloNeurons.Get(neurons, compiledMarker.Marker, ModelAddress.Key(brainId))).Generate(request, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (IsRecoverableProviderError(error))
            {
                CreatorLogger(neurons).LogSwallowed(error, "Compiled ILLM neuron unavailable; probing local Ollama fallback.");
            }
        }

        // 2. Direct zero-key local Ollama probe (e.g. RTX 5080 running qwen2.5-coder:14b at http://127.0.0.1:11434)
        // Probe when automatic model selection is active, or when explicitModel names an Ollama model or compiled ILLM preset,
        // unless a custom non-default IModelInference implementation was explicitly injected.
        if ((inference is null || inference is InferenceService)
            && (explicitModel is null
                || explicitModel.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)
                || explicitModel.Contains(':', StringComparison.Ordinal)
                || LLMModel.Find(explicitModel) is not null))
        {
            var ollamaEndpoint = ResolveOllamaEndpoint(neurons);
            var preferredTag = explicitModel is not null && LLMModel.Find(explicitModel) is not null ? null : explicitModel;
            var ollamaTag = await LocalLlmCoder.ProbeLocalOllamaModelAsync(ollamaEndpoint, preferredTag, ct).ConfigureAwait(false);
            if (ollamaTag is not null)
            {
                return await InvokeOllamaDirectAsync(ollamaEndpoint, ollamaTag, request, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            explicitModel is null
                ? "No configured AI provider or local Ollama server is available for Creator."
                : $"Creator model '{explicitModel}' is not configured and no fallback AI provider or local Ollama server is available.");
    }

    private static async Task<ResolvedModel?> ResolveCatalogModelAsync(
        IServiceProvider neurons,
        ModelCatalog catalog,
        string brainId,
        string? explicitModel,
        bool requiresTools,
        CancellationToken ct)
    {
        var candidates = new List<ModelSelection?>();
        if (explicitModel is not null)
        {
            candidates.Add(new ModelSelection(explicitModel));
        }
        candidates.Add(null);

        foreach (var candidate in candidates)
        {
            try
            {
                var resolved = await catalog.Resolve(brainId, candidate, requiresTools, ct).ConfigureAwait(false);
                if (resolved.Provider == AiProvider.Host && neurons.GetService<global::Microsoft.Extensions.AI.IChatClient>() is null)
                {
                    continue;
                }

                return resolved;
            }
            catch (Exception error) when (error is not OperationCanceledException && IsRecoverableProviderError(error))
            {
                CreatorLogger(neurons).LogSwallowed(error, "ModelCatalog candidate could not resolve; trying next Creator model candidate.");
            }
        }

        return null;
    }

    private static ILogger CreatorLogger(IServiceProvider neurons) =>
        neurons.GetService<ILoggerFactory>()?.CreateLogger(typeof(CreatorModel)) ?? OsConsoleLog.For(typeof(CreatorModel));

    private static string ResolveOllamaEndpoint(IServiceProvider neurons)
    {
        var config = neurons.GetService<IConfiguration>();
        if (config?["AI:Ollama:Endpoint"] is { Length: > 0 } ep)
        {
            return ep;
        }
        if (Environment.GetEnvironmentVariable("IntoChatOS__AI__Ollama__Endpoint") is { Length: > 0 } envEp)
        {
            return envEp;
        }
        return DefaultOllamaEndpoint;
    }

    private static async Task<InferenceResult> InvokeOllamaDirectAsync(
        string endpoint,
        string modelTag,
        InferenceRequest request,
        CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(3) };
        var wireMessages = new List<object>(request.Messages.Count);
        foreach (var msg in request.Messages)
        {
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var tr in msg.Content.OfType<AiToolResult>())
                {
                    wireMessages.Add(new { role = "tool", content = tr.ResultJson });
                }
                continue;
            }

            var text = string.Join("\n", msg.Content.OfType<AiText>().Select(t => t.Text));
            var calls = msg.Content.OfType<AiToolCall>().Select(tc => new
            {
                function = new
                {
                    name = tc.Name,
                    arguments = ParseJsonOrEmptyObject(tc.ArgumentsJson),
                },
            }).ToArray();

            wireMessages.Add(calls.Length > 0
                ? new { role = msg.Role, content = text, tool_calls = calls }
                : new { role = msg.Role, content = text });
        }

        var wireTools = request.Tools is { Count: > 0 } tools
            ? tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = ParseJsonOrEmptyObject(t.ParametersJson),
                },
            }).ToArray()
            : null;

        var payload = JsonSerializer.Serialize(new
        {
            model = modelTag,
            stream = false,
            think = false,
            options = new { temperature = 0.1 },
            messages = wireMessages,
            tools = wireTools,
        }, Json);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/chat", content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Ollama /api/chat returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var msgEl = doc.RootElement.GetProperty("message");
        var parts = new List<AiContent>();
        if (msgEl.TryGetProperty("content", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            var s = textEl.GetString();
            if (!string.IsNullOrEmpty(s)) { parts.Add(new AiText(s)); }
        }

        if (msgEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var item in tcEl.EnumerateArray())
            {
                if (!item.TryGetProperty("function", out var fn)) { continue; }
                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var argsJson = fn.TryGetProperty("arguments", out var a)
                    ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText())
                    : "{}";
                parts.Add(new AiToolCall($"call_{idx++}_{Guid.NewGuid():N}"[..16], name, argsJson));
            }
        }

        return new InferenceResult(
            [new AiMessage("assistant", [.. parts])],
            FinishReason: parts.OfType<AiToolCall>().Any() ? "tool_calls" : "stop",
            Usage: null,
            ResponseId: null,
            Model: modelTag);
    }

    private static JsonElement ParseJsonOrEmptyObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return JsonElement.Parse("{}"); }
        try { return JsonElement.Parse(raw); }
        catch (JsonException) { return JsonElement.Parse("{}"); }
    }
}

// The Creator's model calls, as a seam: a scripted model stands here in facts.
internal interface ICreatorModel
{
    Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct);

    Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct);
}

// The brain's own model catalog / local Ollama with deterministic offline synthesizer fallback when unconfigured or offline.
internal sealed class SiloCreatorModel(IServiceProvider neurons) : ICreatorModel
{
    public Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct)
        => CreatorModel.Complete(neurons, brainId, model, system, prompt, ct);

    public Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct)
        => CreatorModel.Generate(neurons, brainId, model, request, ct);
}
