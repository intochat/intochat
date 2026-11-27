using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Identity.Local.Vault;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.Secrets;

public static class MasterKeyServiceCollectionExtensions
{
    public static IServiceCollection AddMasterKeyWrapper(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<MasterKeyOptions>().BindConfiguration(MasterKeyOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.MasterKey),
                $"Configure {IntoChatOSNames.MasterKeyConfigurationKey} via the {IntoChatOSNames.MasterKeyEnvironmentVariable} environment secret before starting the host.")
            .ValidateOnStart();

        services.TryAddSingleton<IMasterKeyCustody>(sp =>
        {
            var master = sp.GetRequiredService<IOptions<MasterKeyOptions>>().Value.MasterKey;
            if (string.IsNullOrWhiteSpace(master))
            {
                throw new InvalidOperationException($"{IntoChatOSNames.MasterKeyConfigurationKey} is not configured.");
            }

            return new ConfiguredMasterKeyCustody(Encoding.UTF8.GetBytes(master));
        });

        services.TryAddSingleton(sp =>
        {
            var custody = sp.GetRequiredService<IMasterKeyCustody>();
            var master = sp.GetService<IOptions<MasterKeyOptions>>()?.Value.MasterKey;
            return string.IsNullOrWhiteSpace(master)
                ? new MasterKeyWrapper(custody)
                : new MasterKeyWrapper(custody, Encoding.UTF8.GetBytes(master));
        });

        services.TryAddSingleton<ILegacyBrainKeyReader>(sp =>
            new BrainStorageLegacyKeyReader(sp.GetService<BrainStorageKeys>()));

        services.TryAddSingleton(sp => new LocalSecretSealer(
            sp.GetRequiredService<IMasterKeyCustody>(),
            sp.GetRequiredService<MasterKeyWrapper>(),
            sp.GetService<ILegacyBrainKeyReader>()));

        services.TryAddSingleton<ISecretSealer>(sp => sp.GetRequiredService<LocalSecretSealer>());
        services.TryAddSingleton<IBrainSecretSealer>(sp => sp.GetRequiredService<LocalSecretSealer>());
        services.AddOsSecretVaultPort();
        return services;
    }

    public static IServiceCollection AddOsSecretVaultPort(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new LocalVaultPort(
            (contract, key) => SiloNeurons.Get(sp, contract, key),
            sp.GetRequiredService<IMasterKeyCustody>(),
            sp.GetService<IConfiguration>(),
            sp.GetService<EdgeAuthority>(),
            sp.GetService<ICallerAmbient>(),
            sp.GetService<IBrainSecretSealer>(),
            new LazyBrainCapabilityPolicy(sp),
            sp.GetService<CachingSecretValueSource>()));
        services.TryAddSingleton<ISecretVaultPort>(sp => sp.GetRequiredService<LocalVaultPort>());
        services.TryAddSingleton(sp =>
        {
            var callers = sp.GetService<ICallerAmbient>()
                ?? (sp.GetService<EdgeAuthority>() is { } edge ? new EdgeCallerAmbient(edge) : EmptyCallerAmbient.Instance);
            return new CachingSecretValueSource(
                (owner, ct) => LoadVaultValuesAsync(sp, owner, ct),
                callers,
                sp.GetService<TimeProvider>(),
                extraAppCheck: GrantRules.IsAppCaller);
        });
        services.TryAddSingleton<ISecretValueSource>(sp => sp.GetRequiredService<CachingSecretValueSource>());
        return services;
    }

    private static async ValueTask<IReadOnlyList<string>> LoadVaultValuesAsync(
        IServiceProvider services,
        string owner,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edge = services.GetService<EdgeAuthority>();
        if (edge is null || string.IsNullOrWhiteSpace(owner))
        {
            return [];
        }

        var vault = SiloNeurons.Get<IVault>(services, IVault.Key(owner));
        return await edge.AsKernel(BrainId.Kernel, () => vault.Values()).ConfigureAwait(false);
    }

    private sealed class BrainStorageLegacyKeyReader(BrainStorageKeys? keys) : ILegacyBrainKeyReader
    {
        public Task<byte[]?> TryGetExistingAsync(string brainId, CancellationToken cancellationToken = default)
            => keys is null
                ? Task.FromResult<byte[]?>(null)
                : keys.TryGetExisting(brainId, cancellationToken);
    }

    private sealed class EmptyCallerAmbient : ICallerAmbient
    {
        public static readonly EmptyCallerAmbient Instance = new();
        public Caller? Current => null;
    }

    private sealed class EdgeCallerAmbient(EdgeAuthority edge) : ICallerAmbient
    {
        public Caller? Current => edge.Current;
    }

    // Defers resolving IBrainCapabilityPolicy (which depends on EphemeralBrainRegistry -> AuthorityMirror)
    // until an actual Set/Seal call executes, avoiding eager startup DI cycles.
    private sealed class LazyBrainCapabilityPolicy(IServiceProvider services) : IBrainCapabilityPolicy
    {
        public CapabilityDecision Evaluate(BrainResidency residency, BrainCapability capability, string? resource = null, int usedTurns = 0)
            => services.GetService<IBrainCapabilityPolicy>() is { } policy
                ? policy.Evaluate(residency, capability, resource, usedTurns)
                : CapabilityDecision.Allow(capability, resource);

        public ValueTask<CapabilityDecision> EvaluateAsync(string brainId, BrainCapability capability, string? resource = null, CancellationToken cancellationToken = default)
            => services.GetService<IBrainCapabilityPolicy>() is { } policy
                ? policy.EvaluateAsync(brainId, capability, resource, cancellationToken)
                : ValueTask.FromResult(CapabilityDecision.Allow(capability, resource));
    }
}
