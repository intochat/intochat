using DigitalBrain.AI;

namespace DigitalBrain.Modules.AI.Tests;

// A provider integration as the deployment provisions it: its key, and an endpoint when the fact serves one.
internal static class ProviderIntegrations
{
    public static TestBrain WithProvider(this TestBrain brain, string provider, string? endpoint = null, string apiKey = "test-only")
    {
        var schema = provider switch
        {
            "OpenAI" => AIModule.OpenAIIntegration,
            "OpenRouter" => AIModule.OpenRouterIntegration,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Only key-based providers have an integration."),
        };
        brain = brain.WithIntegration(schema, "ApiKey", apiKey);
        return endpoint is null ? brain : brain.WithIntegration(schema, "Endpoint", endpoint);
    }
}
