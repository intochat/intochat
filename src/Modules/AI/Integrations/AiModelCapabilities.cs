using DigitalBrain.Capabilities.Connections.Integrations;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

// Reads registrations straight from their grains on every derivation: a capability appears the moment
// its provider becomes Ready and disappears the moment it stops, with no cache to go stale.
internal sealed class AiModelCapabilities(IModuleIntegrations<AIModule> integrations, IOptionsMonitor<AIOptions> options) : ICapabilitySource
{
    public const string LlmKind = "llm";

    public async Task<IntegrationCapability[]> Derive(CancellationToken ct)
    {
        var configuration = options.CurrentValue;
        var capabilities = new List<IntegrationCapability>();
        var textCards = KnownModels.All
            .Where(static card => card.Can.HasFlag(Abilities.Text)
                && (card.Provider == AiProvider.Ollama || AiIntegrations.KeyedProviders.Contains(card.Provider)))
            .ToArray();

        foreach (var provider in textCards.Select(static card => card.Provider).Distinct())
        {
            var integrationId = provider == AiProvider.Ollama ? "ollama" : AiIntegrations.IdOf(provider);
            if (provider != AiProvider.Ollama)
            {
                var snapshot = await integrations.Get(integrationId).ReadAsync(ct);
                if (snapshot.Status != IntegrationStatus.Ready) { continue; }
            }
            else if (string.IsNullOrEmpty(configuration.Ollama.Endpoint)) { continue; }

            capabilities.AddRange(textCards
                .Where(card => card.Provider == provider)
                // A local server being configured does not imply every catalogued local model is installed.
                .Where(card => provider != AiProvider.Ollama || HasOllamaModel(configuration.Ollama, card))
                .Select(card => new IntegrationCapability(integrationId, LlmKind, card.Id.Value, card.Name)));
        }

        return [.. capabilities];
    }

    private static bool HasOllamaModel(OllamaOptions ollama, ModelCard card)
        => ollama.Models.ContainsKey(card.Id.Value)
            || ollama.Models.Keys.Any(key => KnownModels.Find(key)?.Id == card.Id);
}
