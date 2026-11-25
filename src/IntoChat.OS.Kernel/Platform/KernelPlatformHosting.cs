using System.Text;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Host.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Secrets;

namespace IntoChat.OS.Kernel.Platform;

// The silo's own platform pieces, composed right after identity's (PlatformHosting): the brain store's options and key
// custody, operator diagnostics and management endpoints, and the gRPC neuron edge.
public static class KernelPlatformHosting
{
    public static void AddKernelPlatform(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(KernelPlatformRegistration))) { return; }
        services.AddSingleton<KernelPlatformRegistration>();
        services.AddOptions<OsBrainStoreOptions>().BindConfiguration(OsBrainStoreOptions.Key)
            .PostConfigure<IConfiguration, IServiceProvider>((options, configuration, sp) => options.Directory = StateHome.Resolve(configuration, sp.GetService<RuntimeOptions>() ?? PresenceProofValidator.DefaultRuntimeOptions).State);
        services.AddOptions<BrainStoreOptions>().BindConfiguration(OsBrainStoreOptions.Key)
            .PostConfigure<IConfiguration, IServiceProvider>((options, configuration, sp) => options.Directory = StateHome.Resolve(configuration, sp.GetService<RuntimeOptions>() ?? PresenceProofValidator.DefaultRuntimeOptions).State);
        services.AddSiloDiagnostics();
        services.TryAddSingleton<IMasterKeyCustody>(provider => new ConfiguredMasterKeyCustody(
            Encoding.UTF8.GetBytes(provider.GetRequiredService<IOptions<MasterKeyOptions>>().Value.MasterKey)));
        services.TryAddSingleton<IBrainKeyCustody>(provider => new MasterKeyBrainCustodyAdapter(provider.GetRequiredService<IMasterKeyCustody>()));
        services.TryAddSingleton<BrainStorageKeys>();
        services.AddGrpc();
        services.TryAddSingleton<OsNeuronSurface>();
    }

    public static IEndpointRouteBuilder MapKernelPlatform(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RuntimeManagementEndpoints.Map(endpoints);
        endpoints.MapSiloDiagnostics();
        endpoints.MapGrpcService<NeuronsGrpcService>().WithMetadata(GuestSurface.Door.Instance);
        return endpoints;
    }

    private sealed class MasterKeyBrainCustodyAdapter(IMasterKeyCustody inner) : IBrainKeyCustody
    {
        public byte[] Wrap(string accountId, string context, byte[] secret) => inner.Wrap(accountId, context, secret);

        public byte[] Unwrap(string accountId, string context, byte[] wrapped) => inner.Unwrap(accountId, context, wrapped);
    }

    private sealed class KernelPlatformRegistration;
}
