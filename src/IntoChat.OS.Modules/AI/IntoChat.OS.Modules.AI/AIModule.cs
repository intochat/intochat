using IntoChat.OS;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Connections.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.AI;

public sealed class AIModule() : Module<AIOptions>(new ModuleId("ai")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.AI.Agents.Context.IAgentContext).Assembly;

    public static IntegrationSchema[] Integrations => AiIntegrations.All;
    public static IntegrationSchema OpenAIIntegration => AiIntegrations.OpenAI;
    public static IntegrationSchema OpenRouterIntegration => AiIntegrations.OpenRouter;
    public static IntegrationSchema TypeSafeIntegration => AiIntegrations.TypeSafe;
    public static ConnectorDefinition[] Connectors => ModelConnectors.All;

    internal const string ModuleName = "ai";

    // R4-04: what the module needs from its host (AINeurons). Configure keeps the registrations of the neurons that are
    // still grains until Configure leaves with the Framework (06-08).
    public static DigitalBrain.ModuleManifest Manifest => AINeurons.Manifest;

    // The migrated AI neurons (06-05) run as plain neurons on the runtime's host grains.
    public override Type? Neurons => typeof(AINeurons);

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = AIOptionsRuntime.Read(builder.Configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAiCredentials, RegistrationCredentials>();
        services.TryAddSingleton<BrainKeyAvailability>();
        services.TryAddSingleton<ProviderPublicEndpoints>();
        services.TryAddSingleton<IPriceList, ModelPriceList>();
        AIClients.Add(services);
        services.AddHttpClient();
        services.TryAddSingleton<HttpSystemOne>();
        services.TryAddSingleton<AppOperationCatalog>();
        services.AddHostedService<AppMenuHost>();
        services.TryAddSingleton<ModelCatalog>();
        services.TryAddSingleton<InferenceService>();
        services.TryAddSingleton<IModelInference>(provider => provider.GetRequiredService<InferenceService>());
        services.TryAddSingleton<NeuronSurface>();
        services.AddSingleton<ICapabilitySource, AiModelCapabilities>();
        services.AddMediaNeurons();
        AIClients.AddImageGeneration(services, options);
        VoiceToTextHosting.Add(services, options);
    }

    // The calling brain's model list for the shell's model picker; never endpoints or secrets. The
    // brain is the edge's stamped caller; without one the list is the deployment's.
    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/ai/models", async (IIntoChatOS brain) => Results.Ok(await brain.Get<IModelCatalog>(
            CatalogKey(CallerContextStamper.TryGet(out var caller) ? caller : null)).List()));
    }

    // A caller stamped without a valid brain (or no caller) gets the deployment's list, not a 500.
    internal static string CatalogKey(CallerContext? caller)
        => caller is not null && BrainScope.IsValidId(caller.BrainId) && !string.IsNullOrWhiteSpace(caller.AccountId)
            ? IModelCatalog.Key(caller.BrainId) : IModelCatalog.DeploymentKey;
}

// The DigitalBrain module of AI (06-05): its assembly's [Neuron] classes run on the runtime's host grains, on TestOS and on
// the silo alike.
[DigitalBrain.Module(AIModule.ModuleName)]
public sealed class AINeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AIOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId(AIModule.ModuleName)))],
        Secrets: []);
}
