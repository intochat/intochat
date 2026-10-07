using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Contracts;

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
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<KernelNeuron>, KernelActivation>());
        silo.Services.AddOptions<ObserverOptions>().Validate(options => options.Lease > TimeSpan.Zero, "Observer lease must be positive.").ValidateOnStart();
        return silo;
    }
}
