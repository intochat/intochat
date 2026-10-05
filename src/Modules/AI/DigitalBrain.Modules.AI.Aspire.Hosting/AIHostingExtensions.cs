using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.AI.FoundryLocal;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Contracts;
using DigitalBrain.Platform.Contracts.Integrations;

namespace DigitalBrain.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    // "tavily" mirrors the module's internal AiIntegrations.TavilyId.
    private static readonly string TavilyApiKeyEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(IntegrationDefinition.Key("tavily", "ApiKey"));
    private static readonly string TavilyEnabledEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(AIKeys.TavilyEnabled);
    private const string TavilyApiKeyDescription = "API key for Tavily web search. Sign up at [Tavily](https://www.tavily.com/) and copy your API key from your account dashboard. Paste it here to enable web search for DigitalBrain agents.";
    private static readonly string EnableSensitiveDataEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(AIKeys.TelemetryEnableSensitiveData);

    // The declaration API is the typed AIOptions extension family (WithLlm<TModel> and
    // friends): typed at the declaration site, carried by configuration — the one value
    // channel — and resolved loudly once at bind in ConfigureDeclaration.
    private static AIHostingState State(DigitalBrainModuleBuilder<DigitalBrain.AI.Aspire.Hosting.AIModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(brain => new AIHostingState(brain, module.Resource));

        return state;
    }

}
