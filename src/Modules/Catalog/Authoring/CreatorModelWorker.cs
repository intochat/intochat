using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

internal static class CreatorModel
{
    public const string AutoModel = "auto";

    private static bool IsRecoverableProviderError(Exception ex) => ex is InvalidOperationException
        or ArgumentException
        or KeyNotFoundException
        or NotSupportedException
        or HttpRequestException
        or TimeoutException
        or ProviderUnavailableException;

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

        // Route all Creator calls through Modules/AI (ModelCatalog.Decide(ModelUse.Code) -> IModelInference / IChatClient / ILLM).
        var catalog = neurons.GetService<ModelCatalog>();
        var inference = neurons.GetService<IModelInference>();
        if (catalog is not null)
        {
            var resolved = await ResolveCatalogModelAsync(neurons, catalog, brainId, explicitModel, requiresTools, ct).ConfigureAwait(false);
            if (resolved is not null)
            {
                var effectiveInference = inference ?? throw new InvalidOperationException("IModelInference must be registered alongside ModelCatalog.");
                var selection = ToSelection(resolved);
                try
                {
                    return await effectiveInference.Generate(request with { Model = selection }, model: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                }
                catch (Exception error) when (explicitModel is not null && !ct.IsCancellationRequested && IsRecoverableProviderError(error))
                {
                    CreatorLogger(neurons).LogWarning(error, "Explicit Creator model selection failed; retrying automatic brain code model.");
                    var autoResolved = await ResolveCatalogModelAsync(neurons, catalog, brainId, explicitModel: null, requiresTools, ct).ConfigureAwait(false);
                    if (autoResolved is not null)
                    {
                        return await effectiveInference.Generate(request with { Model = ToSelection(autoResolved) }, model: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                    }
                }
            }
        }

        if (inference is not null)
        {
            var selection = explicitModel is null ? null : new ModelSelection(explicitModel);
            try
            {
                var desc = await inference.Describe(selection, model: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                if ((!requiresTools || desc.Tools == CapabilitySupport.Supported)
                    && (!string.Equals(desc.Provider, "Host", StringComparison.OrdinalIgnoreCase)
                        || neurons.GetService<IChatClient>() is not null))
                {
                    return await inference.Generate(request with { Model = selection }, model: null, cancellationToken: ct, brainId: brainId).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested && IsRecoverableProviderError(error))
            {
                CreatorLogger(neurons).LogWarning(error, "IModelInference failed for Creator model '{Model}'.", explicitModel ?? AutoModel);
            }
        }

        if (explicitModel is not null && KnownModels.Find(explicitModel) is { } card)
        {
            return await SiloNeurons.Get<ILLM>(neurons, ILLM.Key(brainId, card.Id)).Generate(request, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            explicitModel is null
                ? "No configured AI provider or local Ollama server is available for Creator."
                : $"Creator model '{explicitModel}' is not configured and no fallback AI provider or local Ollama server is available.");
    }

    private static ModelSelection? ToSelection(ResolvedModel resolved)
        => resolved.Provider == AiProvider.Host ? null
            : resolved.Profile is { Length: > 0 } ? new($"profile:{resolved.Profile}", resolved.Reasoning, resolved.MaxOutputTokens)
            : resolved.Provider == AiProvider.Ollama ? new($"ollama/{resolved.Model}", resolved.Reasoning, resolved.MaxOutputTokens)
            : new(resolved.Model, resolved.Reasoning, resolved.MaxOutputTokens);

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
                var resolved = await catalog.Decide(brainId, ModelUse.Code, candidate, personModel: null, requiresTools, ct).ConfigureAwait(false);
                if (resolved.Provider == AiProvider.Host && neurons.GetService<IChatClient>() is null)
                {
                    continue;
                }

                return resolved;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && IsRecoverableProviderError(error))
            {
                CreatorLogger(neurons).LogWarning(error, "ModelCatalog code candidate could not resolve; checking next candidate.");
            }
        }

        return null;
    }

    private static ILogger CreatorLogger(IServiceProvider neurons) =>
        neurons.GetService<ILoggerFactory>()?.CreateLogger(typeof(CreatorModel)) ?? OsConsoleLog.For(typeof(CreatorModel));
}

// The Creator's model calls, as a seam: a scripted model stands here in facts.
internal interface ICreatorModel
{
    Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct);

    Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct);
}

internal sealed class SiloCreatorModel(IServiceProvider neurons) : ICreatorModel
{
    public Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct)
        => CreatorModel.Complete(neurons, brainId, model, system, prompt, ct);

    public Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct)
        => CreatorModel.Generate(neurons, brainId, model, request, ct);
}
