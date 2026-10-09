using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Framework;

public static class BrainHosting
{
    public static ISiloBuilder AddDigitalBrain(this ISiloBuilder silo)
        => silo.AddDigitalBrain([]);

    public static ISiloBuilder AddDigitalBrain(this ISiloBuilder silo, IEnumerable<Module> modules)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var inventory = new ModuleInventory(modules);
        silo.Services.AddSingleton(inventory);
        foreach (var module in inventory.Modules) { module.AddOptions(silo.Services); }
        silo.Services.AddContractCatalog();
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.Services.TryAddSingleton<ICallFilter, CallFilter>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, NeuronCallFilter>());
        silo.Services.TryAddSingleton<LocalSignalHub>();
        silo.Services.TryAddSingleton<ILocalSignalHub>(sp => sp.GetRequiredService<LocalSignalHub>());
        silo.Services.TryAddSingleton<IDigitalBrain, CurrentBrain>();
        silo.Services.TryAddSingleton<Kernel>();
        silo.Services.TryAddSingleton<IKernel>(sp => sp.GetRequiredService<Kernel>());
        silo.Services.TryAddSingleton<IKernelView>(sp => sp.GetRequiredService<Kernel>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<BrainNeuron>, BrainActivation>());
        silo.Services.AddOptions<ObserverOptions>().Validate(options => options.Lease > TimeSpan.Zero, "Observer lease must be positive.").ValidateOnStart();
        return silo;
    }

    // Wraps the Default grain storage so a neuron's first write registers it with the kernel. Call
    // after the Default storage provider is registered; without one there is nothing to register in.
    public static ISiloBuilder AddKernelRegistration(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (!InstanceRegistration.TryDecorate(silo.Services))
        { throw new InvalidOperationException($"Register the '{DigitalBrainNames.DefaultGrainStorage}' grain storage before the kernel registration."); }
        return silo;
    }

    // For hosts whose Default storage may be composed after this point (configuration-bound
    // providers are registered before the silo delegate runs; a later UseOrleans call is not).
    public static bool TryAddKernelRegistration(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        return InstanceRegistration.TryDecorate(silo.Services);
    }
}
