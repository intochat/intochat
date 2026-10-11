using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        // R105: a machine is a single-silo cluster; a stale Active row of its own previous run must not block its start.
        silo.AsSingleSiloCluster();
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
        // T23-9: the holder of a part audits and repairs its segments; the auditor's clock is the mesh's (a fact moves it).
        services.TryAddKeyedSingleton(Mesh.MeshOptions.Key, (provider, _) => provider.GetRequiredService<TimeProvider>());
        if (!services.Any(d => d.ServiceType == typeof(Mesh.MeshAuditor)))
        {
            services.AddSingleton<Mesh.MeshAuditor>();
            services.AddHostedService(provider => provider.GetRequiredService<Mesh.MeshAuditor>());
        }
        // Single-host composition (no trust endpoint) holds every part itself: nothing to forward.
        if (!string.IsNullOrWhiteSpace(silo.Configuration[$"{Trust.TrustOptions.Key}:{nameof(Trust.TrustOptions.Endpoint)}"])) { silo.AddCrossPartCalls(); }
        services.AddOptions<PartsOptions>().Validate<IHostEnvironment, IServiceProvider>((parts, environment, provider) =>
        {
            ValidateCustody(parts, environment, provider);
            return true;
        });
        return silo;
    }

    // R35/R78/R81: a production own machine holds its custody root in hardware or acknowledges a configured one; a Cloud machine holds only non-private
    // parts (Q24) and may keep Configured custody. Tpm custody needs a module that provides it.
    private static void ValidateCustody(PartsOptions parts, IHostEnvironment environment, IServiceProvider provider)
    {
        const string key = DigitalBrainNames.CustodyConfigurationKey;
        if (parts.Custody == MasterKeyCustodyKind.Configured)
        {
            if (parts.Role != MachineRole.Own || !environment.IsProduction()) { return; }
            // R81: the operator may accept a configured root explicitly; it is said at every start.
            if (parts.ConfiguredCustodyAcknowledged)
            {
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MachineHosting)).LogWarning(
                    "{Key} = Configured on a production own machine (acknowledged): the custody root is a configured secret, not hardware.", key);
                return;
            }
            throw new InvalidOperationException($"{key} = Configured is refused for an own machine in Production; a production own machine holds its master keys in the TPM ({key} = Tpm) or acknowledges a configured root ({PartsOptions.Key}:{nameof(PartsOptions.ConfiguredCustodyAcknowledged)} = true).");
        }
        IMasterKeyCustody custody;
        try { custody = provider.GetRequiredService<IMasterKeyCustody>(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { throw new InvalidOperationException($"{key} = Tpm, but this machine's TPM custody cannot be opened: {exception.Message}", exception); }
        if (custody is Keys.ConfiguredMasterKeyCustody)
        { throw new InvalidOperationException($"{key} = Tpm, but no TPM custody is registered (the Windows module provides it on a Windows machine)."); }
    }
}
