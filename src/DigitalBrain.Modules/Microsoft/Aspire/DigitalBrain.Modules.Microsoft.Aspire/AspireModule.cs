using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireModule() : Module(new ModuleId("aspire")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Microsoft.Aspire.ICluster).Assembly;

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<Verification.ICandidateVerifier, Verification.CandidateVerifier>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<Cluster.ClusterNeuron>, Cluster.ClusterReconcile>());
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        InfrastructureEndpoints.Map(endpoints);
    }
}
