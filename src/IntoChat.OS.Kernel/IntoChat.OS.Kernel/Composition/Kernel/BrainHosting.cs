using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Kernel;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Compute;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Composition.Machine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace IntoChat.OS.Kernel.Composition.Kernel;

public static class BrainHosting
{
    public static ISiloBuilder AddIntoChatOS(this ISiloBuilder silo)
        => silo.AddIntoChatOS([]);

    public static ISiloBuilder AddIntoChatOS(this ISiloBuilder silo, IEnumerable<OsModule> modules)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var selected = modules.ToArray();
        // The host registers what became of every module it was given; a bare silo reports its modules Active.
        silo.Services.AddSingleton(provider => new ModuleInventory(selected, provider.GetService<IReadOnlyList<ModuleStatus>>()));
        foreach (var module in selected) { module.BindOptions(silo.Services); }
        if (selected.Any(module => module.Id.Value == "windows"))
        {
            silo.Services.AddWindowsMachineIdentity();
        }
        silo.Services.TryAddSingleton(provider => new CompiledTypes(provider.GetRequiredService<ModuleInventory>(), provider.GetServices<IModuleTypeSource>()));
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.Services.TryAddSingleton<IOperationHandler<WritePlan>, StorageWriteHandler>();
        DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(silo.Services, typeof(DigitalBrain.Confirmation.ConfirmationRequested).Assembly);
        silo.Services.Replace(ServiceDescriptor.Singleton(new DigitalBrain.Access.AccessOptions { UnstampedEdgeIsKernel = false }));
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, PlainInstanceRegistration>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, IntoChat.OS.Authority.Identity.Grants.SiloAccessCallFilter>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutgoingGrainCallFilter, TraceOutgoingFilter>());
        silo.Services.TryAddSingleton<OsKernel>();
        silo.Services.TryAddSingleton<IKernel>(sp => sp.GetRequiredService<OsKernel>());
        silo.Services.TryAddSingleton<IntoChat.OS.Kernel.Composition.Registry.IRegistryBroker, IntoChat.OS.Kernel.Composition.Registry.RegistryBroker>();
        // AddDigitalBrainRuntime registers ClusterBrainObserver first. The product kernel replaces it: it sees
        // feature types and connection contracts, which the runtime-only observer does not.
        silo.Services.Replace(ServiceDescriptor.Singleton<DigitalBrain.Kernel.IBrainObserver>(sp => sp.GetRequiredService<OsKernel>()));
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
}
