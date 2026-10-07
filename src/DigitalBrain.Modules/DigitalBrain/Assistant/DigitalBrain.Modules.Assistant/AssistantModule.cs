using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Assistant;

public sealed class AssistantModule() : Module(new ModuleId("assistant")), IHttpModule
{
    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<RegistryDiscoveryService>();
        silo.Services.TryAddSingleton(new NeuronContracts(NeuronContracts.Deployed()));
        silo.Services.TryAddSingleton<NeuronSurface>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolFactory, NeuronAgentTools>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolFactory, WorkspaceFormTools>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolFactory, RegistryDiscoveryTools>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolFactory, RegistryInventoryTools>());
        silo.Services.AddOptions<AssistantOptions>().BindConfiguration("Assistant");
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ComputeUsageEndpoints.Map(endpoints);
        AgentEndpoints.Map(endpoints);
    }
}
