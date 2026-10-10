using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;

namespace DigitalBrain.Platform.Parts;

public static class MachineHosting
{
    // A host that names a machine role runs its cluster storage on the part log; without one the silo
    // keeps the cluster storage its server composition registered.
    public static IHostApplicationBuilder AddMachinePartLogWhenConfigured(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(builder.Configuration[$"{PartsOptions.Key}:{nameof(PartsOptions.Role)}"])) { return builder; }
        builder.UseOrleans(silo => silo.AddMachinePartLog());
        return builder;
    }

    // On a machine the cluster grain storage every neuron names is its part's log, wrapped by the kernel
    // registration whether that was added before or after this call (decorating is idempotent).
    public static ISiloBuilder AddMachinePartLog(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.AddDigitalBrainPlatform();
        var services = silo.Services;
        services.TryAddSingleton<PartLogs>();
        silo.AddMachineHandover();
        foreach (var journal in services.Where(d => d.ServiceType == typeof(ISignalJournal)).ToArray()) { services.Remove(journal); }
        services.AddSingleton<ISignalJournal, PartSignalJournal>();
        foreach (var existing in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, DigitalBrainNames.ClusterGrainStorage)).ToArray())
        { services.Remove(existing); }
        services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage, (provider, _) =>
            ActivatorUtilities.CreateInstance<PartGrainStorage>(provider));
        silo.TryAddKernelRegistration();
        return silo;
    }
}
