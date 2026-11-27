using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models.Ollama;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Models;

public sealed partial class ModelCatalog
{
    /// <summary>
    /// A resolved AI role client paired with its <see cref="ResolvedModel"/>, <see cref="ModelDescriptor"/>,
    /// and canonical <see cref="ModelSelection"/>. Disposing disposes the underlying <see cref="IChatClient"/>.
    /// </summary>
    public sealed record ResolvedRoleClient(
        IChatClient Client,
        ResolvedModel Model,
        ModelDescriptor Descriptor,
        ModelSelection? Selection) : IDisposable
    {
        public void Dispose() => Client.Dispose();

        /// <summary>Executes an <see cref="InferenceRequest"/> against <see cref="Client"/> using <see cref="InferenceMapping"/>.</summary>
        public async Task<InferenceResult> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Messages is not { Count: > 0 })
            {
                throw new ArgumentException("At least one message is required.", nameof(request));
            }

            var (_, settings) = InferenceMapping.Merge(Selection ?? request.Model, request.Options);
            InferenceMapping.ValidateRequest(request, Descriptor);
            var options = InferenceMapping.CreateOptions(Model, settings, Descriptor);
            if (request.Tools is { Count: > 0 })
            {
                if (request.Tools.Select(static t => t.Name).Distinct(StringComparer.Ordinal).Count() != request.Tools.Count)
                {
                    throw new ArgumentException("Tool names must be unique.", nameof(request));
                }

                options.Tools = request.Tools
                    .Select(static tool => (AITool)AIFunctionFactory.CreateDeclaration(
                        tool.Name,
                        tool.Description,
                        JsonSerializer.Deserialize<JsonElement>(tool.ParametersJson)))
                    .ToList();
            }

            var messages = request.Messages.Select(InferenceMapping.ToChatMessage).ToList();
            var result = await Client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            return new InferenceResult(
                result.Messages.Select(InferenceMapping.FromChatMessage).ToArray(),
                result.FinishReason?.Value,
                InferenceMapping.Usage(result.Usage),
                result.ResponseId,
                result.ModelId ?? Model.Model);
        }
    }
}

/// <summary>
/// Adapts an <see cref="IModelInference"/> service to <see cref="IChatClient"/> so callers can run
/// <see cref="FunctionInvokingChatClient"/> / <c>UseFunctionInvocation</c> uniformly over either direct
/// provider clients or an <see cref="IModelInference"/> seam.
/// </summary>
internal sealed class InferenceChatClient(
    IModelInference inference,
    ModelSelection? selection,
    string? brainId,
    int? ollamaContextWindowTokens = null) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var req = BuildRequest(messages, options);
        var result = await inference.Generate(req, model: null, cancellationToken: cancellationToken, brainId: brainId)
            .ConfigureAwait(false);

        return new ChatResponse(result.Messages.Select(InferenceMapping.ToChatMessage).ToList())
        {
            ModelId = result.Model,
            ResponseId = result.ResponseId,
            FinishReason = result.FinishReason is { Length: > 0 } fr ? new ChatFinishReason(fr) : null,
            Usage = result.Usage is { } u
                ? new UsageDetails
                {
                    InputTokenCount = u.InputTokens,
                    OutputTokenCount = u.OutputTokens,
                    TotalTokenCount = u.TotalTokens,
                }
                : null,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var req = BuildRequest(messages, options);
        await foreach (var update in inference.GenerateStreaming(req, model: null, cancellationToken: cancellationToken, brainId: brainId)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            var contents = update.Content.Select(InferenceMapping.ToContent).ToList();
            if (update.Usage is { } u)
            {
                contents.Add(new UsageContent(new UsageDetails
                {
                    InputTokenCount = u.InputTokens,
                    OutputTokenCount = u.OutputTokens,
                    TotalTokenCount = u.TotalTokens,
                }));
            }

            yield return new ChatResponseUpdate(
                update.Role is { Length: > 0 } role ? new ChatRole(role) : ChatRole.Assistant,
                contents)
            {
                ModelId = update.Model,
                ResponseId = update.ResponseId,
                MessageId = update.MessageId,
                FinishReason = update.FinishReason is { Length: > 0 } fr ? new ChatFinishReason(fr) : null,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private InferenceRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var aiMessages = messages.Select(InferenceMapping.FromChatMessage).ToList();
        var effectiveCtx = ollamaContextWindowTokens;
        if (effectiveCtx is null
            && options?.AdditionalProperties is { } props
            && props.TryGetValue("num_ctx", out var rawCtx)
            && rawCtx is int intCtx and > 0)
        {
            effectiveCtx = intCtx;
        }

        var provider = effectiveCtx is { } ctx
            ? new OllamaInferenceOptions(ContextWindowTokens: ctx)
            : null;
        var inferenceOptions = options?.Temperature is not null
            || options?.TopP is not null
            || options?.MaxOutputTokens is not null
            || provider is not null
            ? new InferenceOptions(
                Temperature: options?.Temperature,
                TopP: options?.TopP,
                MaxOutputTokens: options?.MaxOutputTokens,
                Provider: provider)
            : null;

        IReadOnlyList<InferenceTool>? tools = null;
        if (options?.Tools is { Count: > 0 } chatTools)
        {
            var converted = new List<InferenceTool>(chatTools.Count);
            foreach (var tool in chatTools)
            {
                if (tool is AIFunction fn)
                {
                    var schemaJson = fn.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? """{"type":"object","properties":{}}"""
                        : fn.JsonSchema.GetRawText();
                    converted.Add(new InferenceTool(fn.Name, fn.Description ?? string.Empty, schemaJson));
                }
            }

            if (converted.Count > 0)
            {
                tools = converted;
            }
        }

        return new InferenceRequest(aiMessages, Model: selection, Options: inferenceOptions, Tools: tools);
    }
}
