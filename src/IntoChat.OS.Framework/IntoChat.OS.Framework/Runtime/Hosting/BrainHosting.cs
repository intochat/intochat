using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace IntoChat.OS.Framework;

public static class BrainHosting
{
    public static ISiloBuilder AddIntoChatOS(this ISiloBuilder silo)
        => silo.AddIntoChatOS([]);

    public static ISiloBuilder AddIntoChatOS(this ISiloBuilder silo, IEnumerable<Module> modules)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var inventory = new ModuleInventory(modules);
        silo.Services.AddSingleton(inventory);
        foreach (var module in inventory.Modules) { module.AddOptions(silo.Services); }
        silo.Services.TryAddSingleton(provider => new CompiledTypes(provider.GetRequiredService<ModuleInventory>()));
        silo.Services.TryAddSingleton(NeuronTypeSearch.Create);
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.Services.TryAddSingleton<IConnectionBindings, NoConnectionBindings>();
        silo.Services.TryAddSingleton<ICallFilter, CallFilter>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, NeuronCallFilter>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutgoingGrainCallFilter, TraceOutgoingFilter>());
        silo.Services.TryAddSingleton<LocalSignalHub>();
        silo.Services.TryAddSingleton<ILocalSignalHub>(sp => sp.GetRequiredService<LocalSignalHub>());
        silo.AddPlacementDirector<BrainAffinePlacement, BrainAffinePlacementDirector>();
        silo.Services.TryAddSingleton<ISignalJournal, MemorySignalJournal>();
        silo.Services.AddOptions<MemorySignalJournalOptions>();
        silo.Services.TryAddSingleton<IIntoChatOS, CurrentBrain>();
        silo.Services.TryAddSingleton<IntoChat.OS.Runtime.INeuronResolver, GrainNeuronResolver>();
        silo.Services.TryAddSingleton<Kernel>();
        silo.Services.TryAddSingleton<IKernel>(sp => sp.GetRequiredService<Kernel>());
        silo.Services.TryAddSingleton<IKernelView>(sp => sp.GetRequiredService<Kernel>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<BrainNeuron>, BrainActivation>());
        silo.Services.AddOptions<ObserverOptions>().Validate(options => options.Lease > TimeSpan.Zero, "Observer lease must be positive.").ValidateOnStart();
        // Ledger 38: bound each "{brain}/{type}" instance directory so a brain's core does not grow without bound.
        silo.Services.AddOptions<InstanceDirectoryOptions>().BindConfiguration(InstanceDirectoryOptions.Key)
            .Validate(options => options.Limit > 0, "The instance directory limit must be positive.").ValidateOnStart();
        silo.Configure<GrainVersioningOptions>(options =>
        {
            options.DefaultCompatibilityStrategy = nameof(BackwardCompatible);
            options.DefaultVersionSelectorStrategy = nameof(LatestVersion);
        });
        return silo;
    }

    // Wraps the cluster grain storage so a neuron's first write registers it with the kernel. Call
    // after the cluster storage provider is registered; without one there is nothing to register in.
    public static ISiloBuilder AddKernelRegistration(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (!InstanceRegistration.TryDecorate(silo.Services))
        { throw new InvalidOperationException($"Register the '{IntoChatOSNames.ClusterGrainStorage}' grain storage before the kernel registration."); }
        return silo;
    }

    // For hosts whose cluster storage may be composed after this point (configuration-bound
    // providers are registered before the silo delegate runs; a later UseOrleans call is not).
    public static bool TryAddKernelRegistration(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        return InstanceRegistration.TryDecorate(silo.Services);
    }

    // Undoes TryAddKernelRegistration so a later composition step can wrap the cluster storage itself; the step
    // then calls TryAddKernelRegistration again. False when the storage is not wrapped.
    public static bool TryRemoveKernelRegistration(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        return InstanceRegistration.TryUndecorate(silo.Services);
    }

    // Whether the resolved cluster storage is the kernel's registration wrapper: what a host checks
    // at startup after TryAddKernelRegistration.
    public static bool HasKernelRegistration(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetKeyedService<Orleans.Storage.IGrainStorage>(IntoChatOSNames.ClusterGrainStorage) is InstanceRegistration;
    }
}
