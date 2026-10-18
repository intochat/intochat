using DigitalBrain.AI;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Integration;

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

    // The deployment's registration of a provider: a kernel grain, not a neuron, so it is reached through the host.
    public static IIntegration Integration(this ITestBrain brain, string id)
        => brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IIntegration>("integration/" + id);
}
