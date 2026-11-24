using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Models;

internal sealed class InferenceService(ModelCatalog catalog) : IModelInference
{
    bool IModelInference.HasProfile(string name) => HasProfile(name);

    Task<ModelDescriptor> IModelInference.Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
        => Describe(selection, model, cancellationToken, brainId);

    Task<InferenceResult> IModelInference.Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        => Generate(request, model, cancellationToken, brainId);

    IAsyncEnumerable<InferenceUpdate> IModelInference.GenerateStreaming(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        => GenerateStreaming(request, model, cancellationToken, brainId);

    internal bool HasProfile(string name) => catalog.HasProfile(name);

    // A keyed LLM neuron pins its card: without a selection it resolves that card, and with one
    // the selection must land on the same model id.
    private async Task<ResolvedModel> ResolveFor(string? brainId, ModelSelection? selection, ModelId? model, bool tools, CancellationToken ct)
    {
        ModelCard? card = null;
        string? pinnedId = null;
        if (model is { Value.Length: > 0 } pinned)
        {
            card = KnownModels.Find(pinned.Value);
            pinnedId = card?.Id.Value ?? pinned.Value.Trim();
            if (string.IsNullOrWhiteSpace(selection?.Model))
            {
                selection = (selection ?? new()) with { Model = pinnedId };
            }
        }

        var resolved = await catalog.Resolve(brainId, selection, tools, ct);
        if (pinnedId is not null)
        {
            var expected = await catalog.Resolve(brainId, new(pinnedId), requiresTools: false, ct);
            var toolsFallbackFromDevice = tools && expected.Provider == AiProvider.Device && resolved.Provider != AiProvider.Device;
            if (!toolsFallbackFromDevice
                && (resolved.Provider != expected.Provider || !string.Equals(resolved.Model, expected.Model, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"The selected profile does not match pinned model '{card?.Id.Value ?? pinnedId}'.", nameof(selection));
            }
        }

        return resolved;
    }

    public async Task<ModelDescriptor> Describe(ModelSelection? selection = null, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
        => catalog.Describe(await ResolveFor(brainId, selection, model, false, cancellationToken));

    public async Task<InferenceResult> Generate(InferenceRequest request, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
    {
        var (resolved, messages, options, selection, settings) = await Prepare(brainId, request, model, cancellationToken);
        if (resolved.Provider == AiProvider.Device)
        {
            var (deviceClient, unavailableReason) = await catalog.TryCreateDeviceRouteClientAsync(brainId, resolved, cancellationToken).ConfigureAwait(false);
            if (deviceClient is not null)
            {
                using (deviceClient)
                {
                    try
                    {
                        var deviceResult = await deviceClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                        return new(deviceResult.Messages.Select(InferenceMapping.FromChatMessage).ToArray(), deviceResult.FinishReason?.Value,
                            InferenceMapping.Usage(deviceResult.Usage), deviceResult.ResponseId, resolved.Model);
                    }
                    catch (DeviceUnavailableException unavailable)
                    {
                        unavailableReason = unavailable.Message;
                    }
                }
            }

            resolved = await catalog.ResolveCloudFallbackAsync(
                brainId,
                ModelRole.Chat,
                selection,
                request.Tools is { Count: > 0 },
                resolved.Model,
                unavailableReason ?? "offline",
                cancellationToken).ConfigureAwait(false);
            options = BuildOptionsFor(request, resolved, settings);
        }

        using var client = await catalog.CreateClient(brainId, resolved, cancellationToken);
        var result = await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return new(result.Messages.Select(InferenceMapping.FromChatMessage).ToArray(), result.FinishReason?.Value,
            InferenceMapping.Usage(result.Usage), result.ResponseId, result.ModelId ?? resolved.Model);
    }

    public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
        InferenceRequest request,
        ModelId? model = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? brainId = null)
    {
        var (resolved, messages, options, selection, settings) = await Prepare(brainId, request, model, cancellationToken);
        if (resolved.Provider == AiProvider.Device)
        {
            var (deviceClient, unavailableReason) = await catalog.TryCreateDeviceRouteClientAsync(brainId, resolved, cancellationToken).ConfigureAwait(false);
            if (deviceClient is not null)
            {
                var receivedFirstDelta = false;
                var fallbackReason = (string?)null;
                using (deviceClient)
                {
                    await using var enumerator = deviceClient.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    while (true)
                    {
                        ChatResponseUpdate update;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                yield break;
                            }
                            update = enumerator.Current;
                        }
                        catch (DeviceUnavailableException unavailable) when (!receivedFirstDelta)
                        {
                            fallbackReason = unavailable.Message;
                            break;
                        }

                        var contents = update.Contents.Where(c => c is not UsageContent).Select(InferenceMapping.FromContent).ToArray();
                        if (contents.Length > 0)
                        {
                            receivedFirstDelta = true;
                        }
                        yield return new(update.Role?.Value, contents,
                            update.FinishReason?.Value, InferenceMapping.Usage(update.Contents.OfType<UsageContent>().LastOrDefault()?.Details),
                            update.ResponseId, update.MessageId, resolved.Model);
                    }
                }

                unavailableReason = fallbackReason;
            }

            resolved = await catalog.ResolveCloudFallbackAsync(
                brainId,
                ModelRole.Chat,
                selection,
                request.Tools is { Count: > 0 },
                resolved.Model,
                unavailableReason ?? "offline",
                cancellationToken).ConfigureAwait(false);
            options = BuildOptionsFor(request, resolved, settings);
        }

        using var client = await catalog.CreateClient(brainId, resolved, cancellationToken);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return new(update.Role?.Value, update.Contents.Where(c => c is not UsageContent).Select(InferenceMapping.FromContent).ToArray(),
                update.FinishReason?.Value, InferenceMapping.Usage(update.Contents.OfType<UsageContent>().LastOrDefault()?.Details),
                update.ResponseId, update.MessageId, update.ModelId ?? resolved.Model);
        }
    }

    private async Task<(ResolvedModel Model, List<ChatMessage> Messages, ChatOptions Options, ModelSelection? Selection, InferenceOptions? Settings)> Prepare(
        string? brainId,
        InferenceRequest request,
        ModelId? model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is not { Count: > 0 }) { throw new ArgumentException("At least one message is required.", nameof(request)); }
        var (selection, settings) = InferenceMapping.Merge(request.Model, request.Options);
        var resolved = await ResolveFor(brainId, selection, model, request.Tools is { Count: > 0 }, cancellationToken);
        var options = BuildOptionsFor(request, resolved, settings);
        return (resolved, request.Messages.Select(InferenceMapping.ToChatMessage).ToList(), options, selection, settings);
    }

    private ChatOptions BuildOptionsFor(InferenceRequest request, ResolvedModel resolved, InferenceOptions? settings)
    {
        var descriptor = catalog.Describe(resolved);
        InferenceMapping.ValidateRequest(request, descriptor);
        var options = InferenceMapping.CreateOptions(resolved, settings, descriptor);
        if (request.Tools is { Count: > 0 })
        {
            if (request.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != request.Tools.Count)
            { throw new ArgumentException("Tool names must be unique.", nameof(request)); }
            options.Tools = request.Tools.Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description,
                JsonSerializer.Deserialize<JsonElement>(tool.ParametersJson))).ToList();
        }
        return options;
    }
}
