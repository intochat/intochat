using DigitalBrain.AI;
using Microsoft.Extensions.Options;

namespace DigitalBrain.AI;

// This is the browser's allowlist. Never serialize a resolved model: it contains
// operator-owned endpoints. Provider credentials remain entirely server-owned.
public sealed class AgentModelCatalog(ModelProfiles profiles, IOptionsMonitor<AIOptions> options, string? defaultModel)
{
    public async Task<ModelCatalog> Read()
    {
        var automatic = await Resolve(Default());
        var choices = new List<ModelChoice>();
        foreach (var choice in Choices())
        {
            var resolved = await Resolve(choice.Selection);
            if (resolved is not null) { choices.Add(Describe(choice.Id, choice.Label, resolved)); }
        }
        return new(Describe(null, "Automatic (workspace default)", automatic), choices.ToArray());
    }

    public async Task<AgentModelSelection?> Select(string? id)
    {
        var selection = id is null ? Default()
            : Choices().FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.Ordinal))?.Selection
                ?? throw new ArgumentException("The selected model is unavailable. Choose a model from Settings.");
        try { await profiles.Resolve(selection, requiresTools: true); }
        catch (ProviderUnavailableException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new ArgumentException("The selected model is unavailable or does not support assistant tools. Choose a model from Settings."); }
        return selection;
    }

    private AgentModelSelection? Default() => defaultModel is { Length: > 0 } model
        ? new(Model: model) : null;

    private IEnumerable<Choice> Choices()
    {
        foreach (var name in options.CurrentValue.ModelProfiles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return new("profile:" + name, name, new(Profile: name));
        }
        foreach (var preset in LLMModel.All)
        {
            // A local server being configured does not imply every compiled local model is installed.
            if (preset.Provider == AiProvider.Ollama && !options.CurrentValue.Ollama.Models.ContainsKey(preset.Marker.Name)) { continue; }
            yield return new("preset:" + preset.Marker.Name, preset.Id, new(Model: preset.Marker.Name));
        }
    }

    private async Task<ResolvedAgentModel?> Resolve(AgentModelSelection? selection)
    {
        try { return await profiles.Resolve(selection, requiresTools: true); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        { return null; }
    }

    private static ModelChoice Describe(string? id, string label, ResolvedAgentModel? model) => new(
        id, label, model?.Provider, model?.Model, model is not null,
        model is null ? [] : Enum.GetValues<LlmCapabilities>()
            .Where(capability => capability != LlmCapabilities.None && model.Capabilities.HasFlag(capability))
            .Select(capability => capability.ToString()).ToArray());

    private sealed record Choice(string Id, string Label, AgentModelSelection Selection);
}
