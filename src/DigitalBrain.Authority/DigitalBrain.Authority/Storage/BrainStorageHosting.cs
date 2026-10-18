using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace DigitalBrain.Authority.Storage;

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
        services.AddOptions<BrainStoreOptions>().BindConfiguration(BrainStoreOptions.Key)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), "Local brain storage needs a directory.")
            .Validate(options => options.Ownership.Kind != BrainLogOwnershipKind.BlobLease
                || options.Ownership.ContainerUri is not null || !string.IsNullOrWhiteSpace(options.Ownership.ConnectionString),
                "Blob lease ownership needs Ownership:ContainerUri or Ownership:ConnectionString.")
            .ValidateOnStart();
        services.TryAddSingleton<BrainStorageKeys>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBrainLogOwnership>(BrainLogOwnership);
        services.TryAddSingleton<BrainLogs>();
        services.RemoveAll<ISignalJournal>();
        services.AddSingleton<ISignalJournal, BrainSignalJournal>();
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, DigitalBrainNames.ClusterGrainStorage)).ToArray())
        { services.Remove(descriptor); }
        services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage, (provider, _) => ActivatorUtilities.CreateInstance<BrainGrainStorage>(provider));
        services.AddSingleton<BrainStorageComposed>();
        // A hosted deployment composes over the local journal here; one registered later applies itself (IsComposed).
        foreach (var extension in HostedStorageExtensions(services)) { extension.Configure(services, silo.Configuration); }
        silo.TryAddKernelRegistration();
        silo.AddStartupTask((provider, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = provider.GetRequiredService<IOptions<BrainStoreOptions>>().Value;
            var configured = provider.GetRequiredService<IMasterKeyCustody>() is Keys.ConfiguredMasterKeyCustody;
            if (options.Custody == MasterKeyCustodyKind.Tpm && configured)
            { throw new InvalidOperationException("TPM custody requires the Windows connector and an available TPM."); }
            if (provider.GetRequiredService<IHostEnvironment>().IsProduction() && configured && !options.ConfiguredCustodyAcknowledged)
            { throw new InvalidOperationException("Production storage requires TPM custody or explicit ConfiguredCustodyAcknowledged."); }
            _ = provider.GetRequiredService<BrainLogs>();
            return Task.CompletedTask;
        });
        return silo;
    }

    private static IBrainLogOwnership BrainLogOwnership(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<BrainStoreOptions>>().Value.Ownership;
        if (options.Kind != BrainLogOwnershipKind.BlobLease) { return FileLockBrainLogOwnership.Instance; }
        var container = options.ConnectionString is { Length: > 0 } connection
            ? new BlobContainerClient(connection, options.ContainerName)
            : new BlobContainerClient(options.ContainerUri!, provider.GetService<TokenCredential>() ?? new DefaultAzureCredential());
        return new BlobLeaseBrainLogOwnership(new AzureBrainLeaseClient(container), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<BlobLeaseBrainLogOwnership>>(), options.ClaimTimeout);
    }

    // Whether AddBrainStorage has run on these services.
    internal static bool IsComposed(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(BrainStorageComposed));

    private static IHostedStorageExtension[] HostedStorageExtensions(IServiceCollection services) =>
        [.. services.Where(d => d.ServiceType == typeof(IHostedStorageExtension)).Select(d => d.ImplementationInstance).OfType<IHostedStorageExtension>()];

    private sealed class BrainStorageComposed;
}

