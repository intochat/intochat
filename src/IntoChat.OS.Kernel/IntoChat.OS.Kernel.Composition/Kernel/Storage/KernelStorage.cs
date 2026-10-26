using System.Reflection;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Identity;
using IntoChat.OS.Framework.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Kernel.Composition;

// The kernel's storage (06-02, R4-09, R4-12): the runtime BrainLog is the cluster store of every os server, through the
// host-bound seam UseClusterStore(new BrainStorageRegistration(moduleAssemblies)). Unmigrated Framework grains persist and
// publish into the same per-brain log through the transitional legacy store and signal journal (deleted in 06-08).
public static class KernelStorage
{
    // On the host builder, before UseTopology: the cluster store registration, and the brain keys under the os master key custody
    // (the configured root, or the Windows TPM), as the os BrainLog had them.
    public static IHostApplicationBuilder UseKernelStorage(this IHostApplicationBuilder builder, IReadOnlyList<Assembly> moduleAssemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(moduleAssemblies);
        builder.Services.TryAddSingleton<IBrainKeyCustody>(static services => new MasterKeyBrainCustody(services.GetRequiredService<IMasterKeyCustody>()));
        builder.UseClusterStore(new BrainStorageRegistration([.. moduleAssemblies]));
        return builder;
    }

    // In the silo, after ConfigureTopologySilo registered the cluster store: the legacy store and journal over the same logs.
    public static ISiloBuilder AddLegacyStorage(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.RemoveAll<ISignalJournal>();
        services.AddSingleton<ISignalJournal, LegacySignalJournal>();
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, IntoChatOSNames.LegacyClusterGrainStorage)).ToArray())
        { services.Remove(descriptor); }
        services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage,
            static (provider, _) => new LegacyGrainStorage(provider.GetRequiredService<IBrainJournalWriter>(), provider.GetRequiredService<Serializer>()));
        if (!services.Any(d => d.ServiceType == typeof(LegacyStorageComposed)))
        {
            var inner = services.LastOrDefault(d => d.ServiceType == typeof(IPersistentStateFactory) && !d.IsKeyedService);
            if (inner is not null) { services.Remove(inner); }
            services.AddSingleton<IPersistentStateFactory>(provider => new LegacyPersistentStateFactory(
                inner is null ? new PersistentStateFactory() : Create<IPersistentStateFactory>(inner, provider),
                provider.GetRequiredService<Orleans.Metadata.GrainClassMap>()));
            services.AddSingleton<LegacyStorageComposed>();
        }
        // A hosted deployment composes over the local journal here; one registered later applies itself.
        foreach (var extension in services.Where(d => d.ServiceType == typeof(IHostedStorageExtension)).Select(d => d.ImplementationInstance).OfType<IHostedStorageExtension>().ToArray())
        { extension.Configure(services, silo.Configuration); }
        silo.AddStartupTask(static (provider, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrainStoreCustody.Validate(provider);
            _ = provider.GetRequiredService<IBrainJournalWriter>();
            return Task.CompletedTask;
        });
        return silo;
    }

    // Whether AddLegacyStorage has run on these services.
    public static bool IsComposed(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(LegacyStorageComposed));

    private static T Create<T>(ServiceDescriptor descriptor, IServiceProvider provider)
        => (T)(descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));

    private sealed class LegacyStorageComposed;

    // The os master key custody wraps the runtime's brain keys (same Wrap/Unwrap shape).
    private sealed class MasterKeyBrainCustody(IMasterKeyCustody custody) : IBrainKeyCustody
    {
        public byte[] Wrap(string accountId, string context, byte[] secret) => custody.Wrap(accountId, context, secret);

        public byte[] Unwrap(string accountId, string context, byte[] wrapped) => custody.Unwrap(accountId, context, wrapped);
    }
}
