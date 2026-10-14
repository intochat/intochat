using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;

namespace DigitalBrain.Platform.Storage;

public static class BrainStorageHosting
{
    public static IHostApplicationBuilder AddBrainStorage(this IHostApplicationBuilder builder)
    {
        builder.UseOrleans(silo => silo.AddBrainStorage());
        return builder;
    }

    public static ISiloBuilder AddBrainStorage(this ISiloBuilder silo)
    {
        var services = silo.Services;
        services.AddOptions<BrainStoreOptions>().BindConfiguration(BrainStoreOptions.Key);
        services.TryAddSingleton<BrainStorageKeys>();
        services.TryAddSingleton<BrainLogs>();
        services.RemoveAll<ISignalJournal>();
        services.AddSingleton<ISignalJournal, BrainSignalJournal>();
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, DigitalBrainNames.ClusterGrainStorage)).ToArray())
        { services.Remove(descriptor); }
        services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage, (provider, _) => ActivatorUtilities.CreateInstance<BrainGrainStorage>(provider));
        silo.TryAddKernelRegistration();
        silo.AddStartupTask((provider, cancellationToken) => { _ = provider.GetRequiredService<BrainLogs>(); return Task.CompletedTask; });
        return silo;
    }
}

