using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Keys;

// T26-5/R127: a circle part key is never under one custody root; the keyring holds it wrapped once per contributed
// machine and generation. A wrap is ephemeral-static ECDH to the target's registered agreement key, HKDF-SHA256 with
// the context `circle|{circle}|{part}`, then AES-256-GCM: a wrap opens only on its machine, for its circle and part.
// Wrap layout: the ephemeral SubjectPublicKeyInfo's length (int32 LE), that key, a 12-byte nonce, the ciphertext, a tag.
internal sealed class CirclePartKeys(IKeyringClient keyring, INodeDirectoryClient directory, IIdentityClient identity, MachineIdentity machine)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // This machine's current-generation key wraps to `machineId` (a contributed machine joins the generation).
    public async Task WrapTo(PartId part, string circleBrainId, string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        cancellationToken.ThrowIfCancellationRequested();
        var (generation, key) = await Current(part, circleBrainId);
        if (!await keyring.AddWraps(circleBrainId, part.Value, generation, [await WrapFor(part, circleBrainId, generation, machineId, key)]))
        { throw new InvalidOperationException($"Part {part} of circle {circleBrainId} rotated past generation {generation} meanwhile."); }
    }

    // The current generation's key, from this machine's own wrap only. The first opener of a part starts generation 1,
    // wrapped to every machine contributed to the circle; a machine with no wrap of the current generation is refused.
    public async Task<byte[]> Unwrap(PartId part, string circleBrainId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await Current(part, circleBrainId)).Key;
    }

    // Every key this machine still holds of the part, newest generation first (R131: what was sealed before a rotation
    // stays readable to whoever holds its key).
    public async Task<byte[][]> Held(PartId part, string circleBrainId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. (await keyring.WrapsOf(circleBrainId, part.Value, machine.MachineId)).Select(wrap => Open(machine.Agreement, part, circleBrainId, wrap.Wrapped))];
    }

    // A new key starts the next generation, wrapped to the machines that remain; older generations are kept (R131).
    public async Task Rotate(PartId part, string circleBrainId, string[] machineIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(machineIds);
        cancellationToken.ThrowIfCancellationRequested();
        var next = await keyring.Generation(circleBrainId, part.Value) + 1;
        var key = RandomNumberGenerator.GetBytes(32);
        if (!await keyring.AddWraps(circleBrainId, part.Value, next, await WrapsFor(part, circleBrainId, next, machineIds, key)))
        { throw new InvalidOperationException($"Part {part} of circle {circleBrainId} rotated past generation {next - 1} meanwhile."); }
    }

    private async Task<(long Generation, byte[] Key)> Current(PartId part, string circleBrainId)
    {
        ArgumentNullException.ThrowIfNull(part);
        while (true)
        {
            var generation = await keyring.Generation(circleBrainId, part.Value);
            var own = (await keyring.WrapsOf(circleBrainId, part.Value, machine.MachineId)).FirstOrDefault(wrap => wrap.Generation == generation);
            if (own is not null) { return (generation, Open(machine.Agreement, part, circleBrainId, own.Wrapped)); }
            if (generation > 0)
            { throw new UnauthorizedAccessException($"Machine {machine.MachineId} holds no key of generation {generation} of part {part} of circle {circleBrainId}."); }
            var key = RandomNumberGenerator.GetBytes(32);
            var machines = await identity.MachinesOf(circleBrainId);
            if (!machines.Contains(machine.MachineId, StringComparer.Ordinal))
            { throw new UnauthorizedAccessException($"Machine {machine.MachineId} is not contributed to circle {circleBrainId}."); }
            if (await keyring.AddWraps(circleBrainId, part.Value, 1, await WrapsFor(part, circleBrainId, 1, machines, key))) { return (1, key); }
        }
    }

    private async Task<CircleKeyWrap[]> WrapsFor(PartId part, string circleBrainId, long generation, string[] machineIds, byte[] key)
    {
        List<CircleKeyWrap> wraps = [];
        foreach (var target in machineIds.Distinct(StringComparer.Ordinal)) { wraps.Add(await WrapFor(part, circleBrainId, generation, target, key)); }
        return [.. wraps];
    }

    private async Task<CircleKeyWrap> WrapFor(PartId part, string circleBrainId, long generation, string machineId, byte[] key)
    {
        var target = await directory.Machine(machineId) ?? throw new UnauthorizedAccessException($"Machine {machineId} is not registered.");
        return new CircleKeyWrap(part.Value, generation, machineId, Wrap(target.AgreementKey, part, circleBrainId, key));
    }

    internal static byte[] Wrap(byte[] agreementKey, PartId part, string circleBrainId, byte[] key)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralKey = ephemeral.ExportSubjectPublicKeyInfo();
        var aesKey = Derive(SoftwareMachineAgreementKey.DeriveWith(ephemeral, agreementKey), ephemeralKey, part, circleBrainId);
        var wrapped = new byte[4 + ephemeralKey.Length + NonceSize + key.Length + TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(wrapped, ephemeralKey.Length);
        ephemeralKey.CopyTo(wrapped, 4);
        var at = 4 + ephemeralKey.Length;
        RandomNumberGenerator.Fill(wrapped.AsSpan(at, NonceSize));
        using var aes = new AesGcm(aesKey, TagSize);
        aes.Encrypt(wrapped.AsSpan(at, NonceSize), key, wrapped.AsSpan(at + NonceSize, key.Length), wrapped.AsSpan(at + NonceSize + key.Length, TagSize));
        return wrapped;
    }

    internal static byte[] Open(IMachineAgreementKey agreement, PartId part, string circleBrainId, byte[] wrapped)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        ArgumentNullException.ThrowIfNull(wrapped);
        var length = wrapped.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(wrapped) : -1;
        if (length <= 0 || wrapped.Length < 4 + length + NonceSize + TagSize) { throw new CryptographicException("A circle key wrap is malformed."); }
        var ephemeralKey = wrapped.AsSpan(4, length).ToArray();
        var aesKey = Derive(agreement.DeriveShared(ephemeralKey), ephemeralKey, part, circleBrainId);
        var at = 4 + length;
        var key = new byte[wrapped.Length - at - NonceSize - TagSize];
        using var aes = new AesGcm(aesKey, TagSize);
        aes.Decrypt(wrapped.AsSpan(at, NonceSize), wrapped.AsSpan(at + NonceSize, key.Length), wrapped.AsSpan(at + NonceSize + key.Length, TagSize), key);
        return key;
    }

    internal static string Context(PartId part, string circleBrainId) => $"circle|{circleBrainId}|{part.Value}";

    private static byte[] Derive(byte[] shared, byte[] ephemeralKey, PartId part, string circleBrainId)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, ephemeralKey, Encoding.UTF8.GetBytes(Context(part, circleBrainId)));
}
