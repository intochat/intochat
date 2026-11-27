using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Keys;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Identity.Keys;

/// <summary>
/// <see cref="IBrainKeySigner"/> backed by <see cref="BrainKeysNeuron"/> inside the Identity authority boundary.
/// Raw private keys never leave <see cref="BrainKeysNeuron"/>; only signatures, ECDH shared secrets, and public keys cross.
/// </summary>
public sealed class IdentityBrainKeySigner : IBrainKeySigner
{
    private readonly IServiceProvider _services;
    private readonly Func<IServiceProvider, BrainId, IBrainKeysCrypto> _resolveCrypto;

    public IdentityBrainKeySigner(IServiceProvider services)
        : this(services, ResolveDefault)
    {
    }

    internal IdentityBrainKeySigner(
        IServiceProvider services,
        Func<IServiceProvider, BrainId, IBrainKeysCrypto> resolveCrypto)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolveCrypto);
        _services = services;
        _resolveCrypto = resolveCrypto;
    }

    /// <inheritdoc />
    public async ValueTask<BrainPublicKeys> GetPublicKeysAsync(BrainId brainId, CancellationToken ct = default)
    {
        var edge = _services.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.Module(IdentityModule.Id, brainId)))
        {
            return await _resolveCrypto(_services, brainId).EnsurePublicKeys(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> SignDataAsync(BrainId brainId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var edge = _services.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.Module(IdentityModule.Id, brainId)))
        {
            return await _resolveCrypto(_services, brainId).SignData(data.ToArray(), ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> DeriveSharedSecretAsync(BrainId brainId, ReadOnlyMemory<byte> peerEphemeralPublicKeySpki, CancellationToken ct = default)
    {
        var edge = _services.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.Module(IdentityModule.Id, brainId)))
        {
            return await _resolveCrypto(_services, brainId).DeriveSharedSecret(peerEphemeralPublicKeySpki.ToArray(), ct).ConfigureAwait(false);
        }
    }

    private static IBrainKeysCrypto ResolveDefault(IServiceProvider services, BrainId brainId)
    {
        var id = IBrainKeys.Id(brainId);
        if (services.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<IBrainKeysCrypto>(id);
        }

        return SiloNeurons.Get<IBrainKeysCrypto>(services, id);
    }
}
