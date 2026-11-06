using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Compute;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Confirmations;
using DigitalBrain.Runtime.Orleans.Enforcement;
using DigitalBrain.Spi;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition.Machine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace IntoChat.OS.Kernel.Composition;

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
        silo.Services.AddOptions<ConfirmationOptions>().BindConfiguration(ConfirmationOptions.Section);
        silo.Services.TryAddSingleton<DigitalBrain.Runtime.Orleans.Operations.IConfirmationContexts, KernelConfirmations>();
        silo.Services.TryAddSingleton<DigitalBrain.Capabilities.Storage.IStorageWriteIdentity, Confirmations.StorageWriteIdentity>();
        silo.Services.TryAddSingleton<DigitalBrain.Capabilities.Storage.IStorageWriteAuthorizer, DigitalBrain.Runtime.Orleans.Operations.StorageWriteAuthorizer>();
        DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(silo.Services, typeof(DigitalBrain.Confirmation.ConfirmationRequested).Assembly);
        silo.Services.TryAddSingleton<DigitalBrain.Runtime.Orleans.Enforcement.ICallFilter, DigitalBrain.Runtime.Orleans.Enforcement.CallFilter>();
        SiloNeurons.AuthorizeStreams(silo.Services, static (services, id, call) =>
        {
            if (services.GetService<DigitalBrain.Runtime.Orleans.Enforcement.ICallFilter>() is not { } filter)
            { return Task.CompletedTask; }
            if (!RuntimeCallerContext.TryGet(out var caller))
            {
                throw new UnauthorizedAccessException($"Unstamped streaming calls are not authorized for plain neuron {call.Contract}.{call.Method}");
            }
            var logger = services.GetService<Microsoft.Extensions.Logging.ILogger<PlainNeuronCallFilter>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PlainNeuronCallFilter>.Instance;
            return PlainNeuronCallFilter.Authorize(filter, logger, caller, id, call);
        });
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, PlainNeuronCallFilter>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, PlainInstanceRegistration>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutgoingGrainCallFilter, TraceOutgoingFilter>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutgoingGrainCallFilter, RuntimeCallerMirror>());
        silo.AddPlacementDirector<BrainAffinePlacement, BrainAffinePlacementDirector>();
        silo.Services.TryAddSingleton<ISignalJournal, MemorySignalJournal>();
        silo.Services.AddOptions<MemorySignalJournalOptions>();
        silo.Services.TryAddSingleton<DigitalBrain.INeuronResolver, GrainNeuronResolver>();
        silo.Services.TryAddSingleton<Kernel>();
        silo.Services.TryAddSingleton<IKernel>(sp => sp.GetRequiredService<Kernel>());
        // AddDigitalBrainRuntime registers ClusterBrainObserver first. The product kernel replaces it: it sees
        // feature types and connection contracts, which the runtime-only observer does not.
        silo.Services.Replace(ServiceDescriptor.Singleton<DigitalBrain.Spi.IBrainObserver>(sp => sp.GetRequiredService<Kernel>()));
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
