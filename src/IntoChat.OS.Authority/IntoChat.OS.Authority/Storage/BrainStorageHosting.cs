using IntoChat.OS.Framework.Modules.Identity;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Storage;

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
            .PostConfigure<Microsoft.Extensions.Configuration.IConfiguration>((options, configuration) => options.Directory = IntoChat.OS.Framework.StateHome.Resolve(configuration).State)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), "Local brain storage needs a directory.")
            .Validate(options => options.Ownership.Kind != BrainLogOwnershipKind.BlobLease
                || options.Ownership.ContainerUri is not null || !string.IsNullOrWhiteSpace(options.Ownership.ConnectionString),
                "Blob lease ownership needs Ownership:ContainerUri or Ownership:ConnectionString.")
            .Validate(options => options.Ownership.ClaimTimeout > TimeSpan.Zero && options.Ownership.ClaimTimeout <= TimeSpan.FromSeconds(25)
                && options.Ownership.WriteTimeout > TimeSpan.Zero && options.Ownership.WriteTimeout < TimeSpan.FromSeconds(20),
                "Ownership:ClaimTimeout must be within the Orleans call timeout (at most 25 s) and Ownership:WriteTimeout under 20 s.")
            .ValidateOnStart();
        services.TryAddSingleton<BrainStorageKeys>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBrainLogOwnership>(BrainLogOwnership);
        services.UseBrainOwnershipCounters<BrainLogOwnershipCounters>();
        services.TryAddSingleton<BrainLogs>();
        services.RemoveAll<ISignalJournal>();
        services.AddSingleton<ISignalJournal, BrainSignalJournal>();
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, IntoChatOSNames.ClusterGrainStorage)).ToArray())
        { services.Remove(descriptor); }
        services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage, (provider, _) => ActivatorUtilities.CreateInstance<BrainGrainStorage>(provider));
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
            : new BlobContainerClient(options.ContainerUri!, Credential(options.ManagedIdentityClientId));
        return new BlobLeaseBrainLogOwnership(new AzureBrainLeaseClient(container, provider.GetRequiredService<ILogger<AzureBrainLeaseClient>>()), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<BlobLeaseBrainLogOwnership>>(), options.ClaimTimeout, options.WriteTimeout);
    }

    // Matches the Orleans storage credential: a user-assigned identity needs its client id, from config or AZURE_CLIENT_ID.
    internal static TokenCredential Credential(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) { clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"); }
        return new DefaultAzureCredential(string.IsNullOrWhiteSpace(clientId) ? new() : new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId.Trim() });
    }

    // Whether AddBrainStorage has run on these services.
    public static bool IsComposed(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(BrainStorageComposed));

    private static IHostedStorageExtension[] HostedStorageExtensions(IServiceCollection services) =>
        [.. services.Where(d => d.ServiceType == typeof(IHostedStorageExtension)).Select(d => d.ImplementationInstance).OfType<IHostedStorageExtension>()];

    private sealed class BrainStorageComposed;
}

