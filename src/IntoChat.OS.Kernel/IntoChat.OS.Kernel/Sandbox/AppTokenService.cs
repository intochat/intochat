using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>Verified identity stamped onto requests from an executing sandboxed app.</summary>
internal sealed record VerifiedAppToken(string BrainId, string AppKey, DateTimeOffset ExpiresUtc);

/// <summary>
/// Mints and validates HMAC-SHA256 tokens binding every sandboxed app deployment to its exact
/// <c>(BrainId, AppKey)</c> identity so code running in the sandbox can never forge a caller identity.
/// </summary>
internal sealed class AppTokenService
{
    private readonly byte[] _secretBytes;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    public AppTokenService(IOptions<SandboxOptions> options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _time = time ?? TimeProvider.System;
        _ttl = TimeSpan.FromMinutes(Math.Clamp(options.Value.TokenTtlMinutes, 1, 1440));
        var configured = options.Value.TokenSigningMaterial;
        _secretBytes = !string.IsNullOrWhiteSpace(configured)
            ? SHA256.HashData(Encoding.UTF8.GetBytes(configured))
            : RandomNumberGenerator.GetBytes(32);
    }

    public string Issue(string brainId, string appKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        var payload = new TokenPayload(brainId, appKey, _time.GetUtcNow().Add(_ttl).ToUnixTimeSeconds());
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = ComputeHmac(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public bool TryVerify(string? token, out VerifiedAppToken? verified)
    {
        verified = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot >= token.Length - 1)
        {
            return false;
        }

        if (!TryBase64UrlDecode(token[..dot], out var payloadBytes)
            || !TryBase64UrlDecode(token[(dot + 1)..], out var signature))
        {
            return false;
        }

        var expected = ComputeHmac(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            return false;
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.BrainId)
            || string.IsNullOrWhiteSpace(payload.AppKey))
        {
            return false;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresUnix);
        if (expires <= _time.GetUtcNow())
        {
            return false;
        }

        verified = new VerifiedAppToken(payload.BrainId, payload.AppKey, expires);
        return true;
    }

    private byte[] ComputeHmac(byte[] data)
    {
        using var hmac = new HMACSHA256(_secretBytes);
        return hmac.ComputeHash(data);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string segment, out byte[] bytes)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1:
                bytes = [];
                return false;
        }

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private sealed record TokenPayload(string BrainId, string AppKey, long ExpiresUnix);
}
