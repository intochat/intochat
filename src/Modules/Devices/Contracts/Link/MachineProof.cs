using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices.Link;

public sealed record MachineProofHeader(
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("typ")] string Typ);

// htm = HTTP method, htu = absolute URL (query included), iat = unix seconds, jti = nonce,
// ath = base64url SHA-256 of the access token.
public sealed record MachineProofPayload(
    [property: JsonPropertyName("htm")] string Htm,
    [property: JsonPropertyName("htu")] string Htu,
    [property: JsonPropertyName("iat")] long Iat,
    [property: JsonPropertyName("jti")] string Jti,
    [property: JsonPropertyName("ath")] string Ath);

public enum ProofError
{
    None,
    Malformed,
    Algorithm,
    Signature,
    Skew,
    Binding,
    Replay,
}

public sealed record ProofResult(ProofError Error)
{
    public bool IsValid => Error == ProofError.None;
}

// The server's record of seen proof nonces; TryAdd is false when the jti was already used and has not expired.
public interface INonceCache
{
    bool TryAdd(string jti, DateTimeOffset expires);
}

// Proof of possession of the machine key: an ES256 compact JWS whose signature is
// IEEE P1363 r||s over SHA-256 of the signing input. Only ES256 is accepted.
public static class MachineProof
{
    public const string Algorithm = "ES256";
    public const string Type = "intochat-machine-proof";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(60);

    public static string Sign(ECDsa key, string method, Uri url, string accessToken, DateTimeOffset now, string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new MachineProofHeader(Algorithm, Type), DeviceWireJson.Default.MachineProofHeader));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(
            new MachineProofPayload(method.ToUpperInvariant(), url.AbsoluteUri, now.ToUnixTimeSeconds(), nonce ?? NewNonce(), AccessTokenHash(accessToken)),
            DeviceWireJson.Default.MachineProofPayload));
        var input = header + "." + payload;
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return input + "." + Encode(signature);
    }

    // `sign` signs the given bytes with the machine key (SHA-256 then P-256, P1363 output); the private key never
    // reaches this type.
    public static async ValueTask<string> CreateAsync(
        Func<ReadOnlyMemory<byte>, ValueTask<byte[]>> sign, string method, Uri url, string accessToken, DateTimeOffset now, string nonce)
    {
        ArgumentNullException.ThrowIfNull(sign);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new MachineProofHeader(Algorithm, Type), DeviceWireJson.Default.MachineProofHeader));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(
            new MachineProofPayload(method.ToUpperInvariant(), url.AbsoluteUri, now.ToUnixTimeSeconds(), nonce, AccessTokenHash(accessToken)),
            DeviceWireJson.Default.MachineProofPayload));
        var input = header + "." + payload;
        var signature = await sign(Encoding.ASCII.GetBytes(input)).ConfigureAwait(false);
        return input + "." + Encode(signature);
    }

    public static ProofResult Verify(
        ReadOnlySpan<byte> machinePublicKeySpki, string proof, string method, Uri url, string accessToken, DateTimeOffset now, INonceCache nonces)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(nonces);
        var parts = (proof ?? string.Empty).Split('.');
        if (parts.Length != 3
            || !TryDecode(parts[0], out var headerBytes) || !TryDecode(parts[1], out var payloadBytes) || !TryDecode(parts[2], out var signature)
            || !TryRead(headerBytes, payloadBytes, out var header, out var payload))
        {
            return new(ProofError.Malformed);
        }

        if (!string.Equals(header.Alg, Algorithm, StringComparison.Ordinal) || !string.Equals(header.Typ, Type, StringComparison.Ordinal))
        {
            return new(ProofError.Algorithm);
        }

        if (!SignatureHolds(machinePublicKeySpki, parts[0] + "." + parts[1], signature)) { return new(ProofError.Signature); }

        var issued = DateTimeOffset.FromUnixTimeSeconds(payload.Iat);
        if ((now - issued).Duration() > MaxSkew) { return new(ProofError.Skew); }
        if (!string.Equals(payload.Htm, method.ToUpperInvariant(), StringComparison.Ordinal)
            || !string.Equals(payload.Htu, url.AbsoluteUri, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(payload.Ath), Encoding.ASCII.GetBytes(AccessTokenHash(accessToken))))
        {
            return new(ProofError.Binding);
        }

        // Last, so only a proof that is otherwise valid consumes its nonce.
        return nonces.TryAdd(payload.Jti, issued + MaxSkew + MaxSkew) ? new(ProofError.None) : new(ProofError.Replay);
    }

    public static string AccessTokenHash(string accessToken)
        => Encode(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken ?? string.Empty)));

    // The machine fingerprint shown in status: the first 16 base64url characters of SHA-256 over the SPKI.
    public static string Fingerprint(ReadOnlySpan<byte> machinePublicKeySpki) => Encode(SHA256.HashData(machinePublicKeySpki))[..16];

    public static string NewNonce() => Encode(RandomNumberGenerator.GetBytes(16));

    private static bool SignatureHolds(ReadOnlySpan<byte> spki, string signingInput, byte[] signature)
    {
        if (signature.Length != 64) { return false; }
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out _);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return key.KeySize == 256
            && key.VerifyData(Encoding.ASCII.GetBytes(signingInput), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static bool TryRead(byte[] headerBytes, byte[] payloadBytes, out MachineProofHeader header, out MachineProofPayload payload)
    {
        header = null!;
        payload = null!;
        try
        {
            var readHeader = JsonSerializer.Deserialize(headerBytes, DeviceWireJson.Default.MachineProofHeader);
            var readPayload = JsonSerializer.Deserialize(payloadBytes, DeviceWireJson.Default.MachineProofPayload);
            if (readHeader?.Alg is null || readHeader.Typ is null
                || readPayload?.Htm is null || readPayload.Htu is null || readPayload.Jti is null || readPayload.Ath is null)
            {
                return false;
            }

            (header, payload) = (readHeader, readPayload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    private static bool TryDecode(string text, out byte[] bytes)
    {
        try
        {
            bytes = Base64Url.DecodeFromChars(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
