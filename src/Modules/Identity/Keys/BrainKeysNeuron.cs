using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Identity.Contracts.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Identity.Keys;

// What is stored for a brain's key pair: each private half as PKCS#8, base64, sealed under the brain's own key
// (IBrainSecretSealer), and the public halves in the clear. It is not exportable state: a sealed value opens only under the
// key of the installation that sealed it.
internal sealed class BrainKeysState
{
    public SealedSecret? SigningKey { get; set; }
    public SealedSecret? ExchangeKey { get; set; }
    public byte[]? SigningPublicKey { get; set; }
    public byte[]? ExchangePublicKey { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BrainKeysState))]
[JsonSerializable(typeof(BrainPublicKeys))]
internal sealed partial class BrainKeysJson : JsonSerializerContext;

[Neuron(IBrainKeys.Type)]
internal sealed class BrainKeysNeuron(IServiceProvider services) : Neuron<BrainKeysState>, IBrainKeys, IBrainKeysCrypto
{
    internal const string SigningName = "brain-keys/signing";
    internal const string ExchangeName = "brain-keys/exchange";

    private IBrainSecretSealer Sealer => services.GetService<IBrainSecretSealer>()
        ?? throw new InvalidOperationException("No IBrainSecretSealer is registered: a brain's key pair is kept sealed or not at all.");

    public Task<BrainPublicKeys?> PublicKeys(CancellationToken ct = default)
    {
        RequireRoster();
        return Task.FromResult(Held());
    }

    public async Task<BrainPublicKeys> Ensure(CancellationToken ct = default)
    {
        RequireRoster();
        if (Held() is { } held) { return held; }

        using var generated = BrainKeyPair.Generate();
        return await Keep(generated.SigningKey.ExportPkcs8PrivateKey(), generated.ExchangeKey.ExportPkcs8PrivateKey());
    }

    public async Task<BrainPublicKeys> Adopt(byte[] signingKeyPkcs8, byte[] exchangeKeyPkcs8, CancellationToken ct = default)
    {
        RequireRoster();
        ArgumentNullException.ThrowIfNull(signingKeyPkcs8);
        ArgumentNullException.ThrowIfNull(exchangeKeyPkcs8);
        // A brain that holds a pair keeps it: adopting again, with the same pair or another, changes nothing.
        if (Held() is { } held) { return held; }

        return await Keep(signingKeyPkcs8, exchangeKeyPkcs8);
    }

    async Task<BrainPublicKeys> IBrainKeysCrypto.EnsurePublicKeys(CancellationToken ct)
    {
        RequireIdentityAuthority();
        return await EnsureInternal();
    }

    async Task<byte[]> IBrainKeysCrypto.SignData(byte[] data, CancellationToken ct)
    {
        RequireIdentityAuthority();
        ArgumentNullException.ThrowIfNull(data);
        await EnsureInternal();

        var sealedSigning = State.SigningKey
            ?? throw new CryptographicException($"Brain '{Id.Brain.Value}' holds no sealed signing key.");
        byte[]? signingPkcs8 = null;
        try
        {
            signingPkcs8 = Convert.FromBase64String(await Sealer.Open(Id.Brain.Value, sealedSigning));
            using var signing = ECDsa.Create();
            signing.ImportPkcs8PrivateKey(signingPkcs8, out _);
            return signing.SignData(data, HashAlgorithmName.SHA256);
        }
        finally
        {
            if (signingPkcs8 is not null) { CryptographicOperations.ZeroMemory(signingPkcs8); }
        }
    }

    async Task<byte[]> IBrainKeysCrypto.DeriveSharedSecret(byte[] peerEphemeralPublicKeySpki, CancellationToken ct)
    {
        RequireIdentityAuthority();
        ArgumentNullException.ThrowIfNull(peerEphemeralPublicKeySpki);
        await EnsureInternal();

        var sealedExchange = State.ExchangeKey
            ?? throw new CryptographicException($"Brain '{Id.Brain.Value}' holds no sealed exchange key.");
        byte[]? exchangePkcs8 = null;
        try
        {
            exchangePkcs8 = Convert.FromBase64String(await Sealer.Open(Id.Brain.Value, sealedExchange));
            using var exchange = ECDiffieHellman.Create();
            exchange.ImportPkcs8PrivateKey(exchangePkcs8, out _);
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerEphemeralPublicKeySpki, out _);
            return exchange.DeriveRawSecretAgreement(peer.PublicKey);
        }
        finally
        {
            if (exchangePkcs8 is not null) { CryptographicOperations.ZeroMemory(exchangePkcs8); }
        }
    }

    private async Task<BrainPublicKeys> EnsureInternal()
    {
        if (Held() is { } held) { return held; }

        using var generated = BrainKeyPair.Generate();
        return await Keep(generated.SigningKey.ExportPkcs8PrivateKey(), generated.ExchangeKey.ExportPkcs8PrivateKey());
    }

    private BrainPublicKeys? Held()
        => State is { SigningKey: not null, ExchangeKey: not null, SigningPublicKey: { Length: > 0 } signing, ExchangePublicKey: { Length: > 0 } exchange }
            ? new BrainPublicKeys(signing, exchange)
            : null;

    // Seals both private halves, opens what was sealed and compares before anything is stored: a pair that would not come
    // back is never the brain's pair.
    private async Task<BrainPublicKeys> Keep(byte[] signingKeyPkcs8, byte[] exchangeKeyPkcs8)
    {
        var publicKeys = PublicKeysOf(signingKeyPkcs8, exchangeKeyPkcs8);
        var brain = Id.Brain.Value;
        var sealer = Sealer;
        var signing = await sealer.Seal(brain, SigningName, Convert.ToBase64String(signingKeyPkcs8));
        var exchange = await sealer.Seal(brain, ExchangeName, Convert.ToBase64String(exchangeKeyPkcs8));
        var opened = PublicKeysOf(
            Convert.FromBase64String(await sealer.Open(brain, signing)),
            Convert.FromBase64String(await sealer.Open(brain, exchange)));
        if (!opened.SigningKey.AsSpan().SequenceEqual(publicKeys.SigningKey) || !opened.ExchangeKey.AsSpan().SequenceEqual(publicKeys.ExchangeKey))
        {
            throw new CryptographicException("The sealed key pair did not open to the pair that was sealed; nothing was stored.");
        }

        await Store(new BrainKeysState
        {
            SigningKey = signing,
            ExchangeKey = exchange,
            SigningPublicKey = publicKeys.SigningKey,
            ExchangePublicKey = publicKeys.ExchangeKey,
        });
        return publicKeys;
    }

    internal static BrainPublicKeys PublicKeysOf(byte[] signingKeyPkcs8, byte[] exchangeKeyPkcs8)
    {
        using var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(signingKeyPkcs8, out _);
        using var exchange = ECDiffieHellman.Create();
        exchange.ImportPkcs8PrivateKey(exchangeKeyPkcs8, out _);
        return new BrainPublicKeys(signing.ExportSubjectPublicKeyInfo(), exchange.ExportSubjectPublicKeyInfo());
    }

    // The one caller is the friend roster of this brain: the call the kernel stamped came through that neuron's own proxy,
    // from inside the cluster. Who started the roster's call (the owner, or a friend's brain during a handshake) is the
    // roster's business; an app, an agent or a person that calls this neuron itself has no roster behind it and is refused.
    private void RequireRoster()
    {
        if (Context.Caller is { Inside: true, Via: { } via }
            && via.Brain == Id.Brain
            && string.Equals(via.Type, IBrainKeys.RosterType, StringComparison.Ordinal))
        {
            return;
        }

        throw new UnauthorizedAccessException("Only the brain's own friend roster uses the brain's keys.");
    }

    private void RequireIdentityAuthority()
    {
        if (Context.Caller is { Inside: true, Actor: Actor.Module module }
            && string.Equals(module.Id, IdentityModule.Id, StringComparison.Ordinal)
            && Context.Caller.Brain == Id.Brain)
        {
            return;
        }

        throw new UnauthorizedAccessException("Only the Identity module authority may perform cryptographic operations on the brain's keys.");
    }
}
