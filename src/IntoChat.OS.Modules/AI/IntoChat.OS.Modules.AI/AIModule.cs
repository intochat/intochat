using IntoChat.OS;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Media;
using IntoChat.OS.AI.Routing;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Pricing;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Framework.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.AI;

public sealed class AIModule() : Module<AIOptions>(new ModuleId("ai")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.AI.Agents.Context.IAgentContext).Assembly;

    public static IntegrationSchema[] Integrations => AiIntegrations.All;
    public static IntegrationSchema OpenAIIntegration => AiIntegrations.OpenAI;
    public static IntegrationSchema OpenRouterIntegration => AiIntegrations.OpenRouter;
    public static IntegrationSchema TypeSafeIntegration => AiIntegrations.TypeSafe;
    public static ConnectorDefinition[] Connectors => ModelConnectors.All;

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = AIOptionsRuntime.Read(builder.Configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAiCredentials, RegistrationCredentials>();
        services.TryAddSingleton<BrainKeyAvailability>();
        services.TryAddSingleton<IPriceList, ModelPriceList>();
        AIClients.Add(services);
        services.AddHttpClient();
        services.TryAddSingleton<HttpSystemOne>();
        services.TryAddSingleton<AppOperationCatalog>();
        services.AddHostedService<AppMenuHost>();
        services.TryAddSingleton<ModelCatalog>();
        services.TryAddSingleton<InferenceService>();
        services.TryAddSingleton<NeuronSurface>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<AgentNeuron>, AgentResume>());
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
            CallerContextStamper.TryGet(out _) ? new NeuronId("default").Within(BrainScope.CurrentId()).Name : "default").List()));
    }
}
