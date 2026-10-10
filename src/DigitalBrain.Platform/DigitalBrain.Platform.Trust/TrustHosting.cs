using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Platform;

public static class TrustHosting
{
    // The trust cluster: sequencer, node directory and machine registry, keyrings, identity, ledger and the
    // Marketplace catalog, all on `platform` storage. It composes no neuron, no module and no custody (R30, R33):
    // it never sees a secret, a credential or an unwrapped key. The relay endpoint arrives with the transport (Task 21).
    public static ISiloBuilder AddTrustCluster(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(TrustRegistration))) { return silo; }
        services.AddSingleton<TrustRegistration>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
        services.AddOptions<MeshOptions>().BindConfiguration(MeshOptions.Key)
            .Validate(mesh => { mesh.Validate(); return true; }).ValidateOnStart();
        silo.AddStartupTask<SeedNodeRegistration>();
        return silo;
    }

    // A separate trust host refuses unsigned trust writes from outside its cluster (R63). A machine composing the trust
    // grains in its own silo (Stage A) has no such boundary to keep.
    public static ISiloBuilder AddTrustBoundary(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.AddIncomingGrainCallFilter<Trust.TrustBoundaryFilter>();
        return silo;
    }

    private sealed class TrustRegistration;
}
