using System.Text.Json;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Kernel-side bridge for <see cref="IntoChat.Sdk.IAi"/>: dispatches prompts and structured extractions
/// through the deployment's active LLM model catalog when available.
/// </summary>
internal sealed class SdkAiAdapter(IServiceProvider services)
{
    public async Task<string> AskAsync(string brainId, string prompt, string? systemPrompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        using (InProcessCallerScope.EnterKernel(brainId))
        {
            var catalog = SiloNeurons.Get<IModelCatalog>(services, IModelCatalog.Key(brainId));
            var choices = await catalog.List().WaitAsync(cancellationToken).ConfigureAwait(false);
            var chosen = choices.Automatic is { Available: true } ? choices.Automatic
                : choices.Models.FirstOrDefault(model => model.Available);

            if (chosen is null || string.IsNullOrWhiteSpace(chosen.Id))
            {
                throw new InvalidOperationException("No AI model is currently configured for this brain.");
            }

            var messages = new List<AiMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new AiMessage("system", [new AiText(systemPrompt)]));
            }

            messages.Add(new AiMessage("user", [new AiText(prompt)]));

            var llm = SiloNeurons.Get<ILLM>(services, $"{brainId}/default");
            var result = await llm.Generate(new InferenceRequest(messages), cancellationToken).ConfigureAwait(false);
            return string.Join(
                Environment.NewLine,
                result.Messages.SelectMany(m => m.Content).OfType<AiText>().Select(t => t.Text)).Trim();
        }
    }

    public async Task<string> ExtractJsonAsync(string brainId, string prompt, string? targetTypeHint, CancellationToken cancellationToken)
    {
        var instruction = string.IsNullOrWhiteSpace(targetTypeHint)
            ? "Respond with valid minified JSON only."
            : $"Respond with valid minified JSON matching {targetTypeHint} only.";
        var raw = await AskAsync(brainId, prompt, instruction, cancellationToken).ConfigureAwait(false);
        return TrimJsonFence(raw);
    }

    private static string TrimJsonFence(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```json".Length..].Trim();
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed["```".Length..].Trim();
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^"```".Length].Trim();
        }

        using var _ = JsonDocument.Parse(trimmed);
        return trimmed;
    }
}
