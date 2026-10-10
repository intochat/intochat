using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;

namespace DigitalBrain.Platform.Parts;

public static class MachineHosting
{
    // On a machine the cluster grain storage every neuron names is its part's log, wrapped by the kernel
    // registration whether that was added before or after this call (decorating is idempotent).
    public static ISiloBuilder AddMachinePartLog(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.AddDigitalBrainPlatform();
        var services = silo.Services;
        services.TryAddSingleton<PartLogs>();
        foreach (var existing in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, DigitalBrainNames.ClusterGrainStorage)).ToArray())
        { services.Remove(existing); }
        services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage, (provider, _) =>
            ActivatorUtilities.CreateInstance<PartGrainStorage>(provider));
        silo.TryAddKernelRegistration();
        return silo;
    }
}
