using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using IntoChat.OS.Host.Surface;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>Verified identity stamped onto requests from an executing sandboxed app.</summary>
internal sealed record VerifiedAppToken(
    string BrainId,
    string AppKey,
    DateTimeOffset ExpiresUtc,
    string[]? AllowedContracts = null)
{
    /// <summary>True when the token is restricted strictly to <see cref="AllowedContracts"/>.</summary>
    public bool IsScoped => AllowedContracts is not null;
}

/// <summary>
/// Mints and validates HMAC-SHA256 tokens binding every sandboxed app deployment to its exact
/// <c>(BrainId, AppKey)</c> identity and optional least-privilege <c>AllowedContracts</c> scope so
/// code running in the sandbox can never forge a caller identity or widen its granted contract scope.
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
        => IssueCore(ParseBrain(brainId), appKey, allowedContracts: null, ttl: null);

    /// <summary>
    /// Mints a least-privilege token restricted strictly to the specified <paramref name="allowedContracts"/>
    /// (for example <c>["github", "notifications"]</c>). Passing an empty collection issues a zero-contract token
    /// that rejects every <c>/edge/sandbox/invoke</c> call.
    /// </summary>
    public string IssueScoped(
        BrainId brainId,
        string appKey,
        IEnumerable<string> allowedContracts,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(allowedContracts);
        var normalized = NeuronContractDiscovery.NormalizeScopeList(allowedContracts);
        var forbidden = normalized.Where(NeuronContractDiscovery.IsAuthorityContract).ToArray();
        if (forbidden.Length > 0)
        {
            throw new UnauthorizedAccessException(
                $"Cannot mint a scoped AppToken granting authority contract(s): {string.Join(", ", forbidden)}.");
        }

        return IssueCore(brainId, appKey, normalized, ttl);
    }

    private string IssueCore(BrainId brainId, string appKey, string[]? allowedContracts, TimeSpan? ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        var effectiveTtl = ttl.HasValue
            ? TimeSpan.FromTicks(Math.Clamp(ttl.Value.Ticks, TimeSpan.FromSeconds(1).Ticks, _ttl.Ticks))
            : _ttl;
        var payload = new TokenPayload(
            brainId.Value,
            appKey,
            _time.GetUtcNow().Add(effectiveTtl).ToUnixTimeSeconds(),
            allowedContracts);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = ComputeHmac(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    private static BrainId ParseBrain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.StartsWith('@') ? BrainId.System(value[1..]) : new BrainId(value);
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

        var scopes = payload.AllowedContracts is null
            ? null
            : NeuronContractDiscovery.NormalizeScopeList(payload.AllowedContracts);

        verified = new VerifiedAppToken(payload.BrainId, payload.AppKey, expires, scopes);
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

    private sealed record TokenPayload(
        string BrainId,
        string AppKey,
        long ExpiresUnix,
        string[]? AllowedContracts = null);
}
