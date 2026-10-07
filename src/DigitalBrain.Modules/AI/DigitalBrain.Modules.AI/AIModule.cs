using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Media;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Tools;
using DigitalBrain.AI.Tools.WebSearch;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.AI;

public sealed class AIModule() : Module<AIOptions>(new ModuleId("ai"))
{
    public static readonly string McpServersKey = AIKeys.McpServers;

    public static IntegrationSchema[] Integrations => AiIntegrations.Definitions;
    public static ConnectorDefinition[] Connectors => ModelConnectors.All;

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        AIOptionsRuntime.Register(services);
        var options = AIOptionsRuntime.Read(builder.Configuration);
        var workspace = builder.Configuration.GetSection(AIWorkspaceOptions.SectionName).Get<AIWorkspaceOptions>() ?? new();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAiCredentials, RegistrationCredentials>();
        AIClients.Add(services);
        services.TryAddSingleton<ModelCatalog>();
        services.TryAddSingleton<InferenceService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<AgentNeuron>, AgentResume>());
        services.AddSingleton<ICapabilitySource, AiModelCapabilities>();
        services.AddMediaNeurons();
        AIClients.AddImageGeneration(services, options);
        VoiceToTextHosting.Add(services, options);
        WebSearchHosting.Add(services, options);
        NativeToolRegistrations.Add(services, options, workspace);
        McpAgentTools.Register(services, builder.Configuration[McpServersKey]);
    }
}
