using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
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
        BrainId? brain = BrainScope.IsValidId(brainId) ? new BrainId(brainId) : null;

        // Route all Creator calls through ModelCatalog.ResolveRoleClientAsync(ModelRole.Think).
        var candidates = explicitModel is not null
            ? new ModelSelection?[] { new(explicitModel), null }
            : [null];

        foreach (var candidate in candidates)
        {
            try
            {
                using var resolved = await ModelCatalog.ResolveRoleClientAsync(
                    neurons,
                    brain,
                    ModelRole.Think,
                    candidate,
                    requiresTools,
                    invokesTools: false,
                    ct: ct).ConfigureAwait(false);

                return await resolved.GenerateAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && IsRecoverableProviderError(error))
            {
                CreatorLogger(neurons).LogWarning(
                    error,
                    candidate is not null
                        ? "Explicit Creator model selection failed; retrying automatic brain code model."
                        : "ModelCatalog code model resolution failed for Creator.");
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
