using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.External;

/// <summary>
/// Signs outbound ES256 <see cref="ServiceAssertion"/> JWTs using the P-256 private key configured under
/// <c>IntoChatOS:Auth:ServiceSigner</c>. Configuration is validated on start (<see cref="ServiceSignerValidation"/>).
/// </summary>
internal sealed partial class ServiceAssertionSigner : IServiceAssertionSigner, IDisposable
{
    private readonly ECDsa? _key;
    private readonly string? _issuer;
    private readonly string? _audience;
    private readonly TimeProvider _clock;

    public ServiceAssertionSigner(IOptions<AuthOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;

        var signer = options.Value.ServiceSigner;
        if (IsUnconfigured(signer))
        {
            return;
        }

        _issuer = signer!.Issuer.Trim();
        _audience = signer.Audience.Trim();
        ServiceUri = new Uri(signer.ServiceUrl.Trim(), UriKind.Absolute);
        _key = ImportPrivateKey(signer.PrivateKey);
    }

    public bool IsConfigured => _key is not null && ServiceUri is not null;

    public Uri? ServiceUri { get; }

    public string Sign(string brainId, string purpose, string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        if (!IsConfigured || _key is null || _issuer is null || _audience is null)
        {
            throw new InvalidOperationException("Outbound service-assertion signing is not configured.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        var now = _clock.GetUtcNow();
        var claims = new ServiceAssertionClaims(
            Issuer: _issuer,
            Audience: _audience,
            Operation: purpose,
            Request: ServiceAssertion.Request(method, pathAndQuery, body),
            Jti: Guid.NewGuid().ToString("N"),
            ExpiresAt: now.AddSeconds(ServiceAssertion.LifetimeSeconds),
            Subject: brainId.Trim(),
            Purpose: purpose);

        return ServiceAssertion.Sign(claims, _key, _clock);
    }

    public void Dispose() => _key?.Dispose();

    internal static bool IsUnconfigured(ServiceSignerOptions? signer)
        => signer is null
            || (string.IsNullOrWhiteSpace(signer.Issuer)
                && string.IsNullOrWhiteSpace(signer.Audience)
                && string.IsNullOrWhiteSpace(signer.ServiceUrl)
                && string.IsNullOrWhiteSpace(signer.PrivateKey));

    internal static string? Problem(ServiceSignerOptions? signer)
    {
        if (IsUnconfigured(signer))
        {
            return null;
        }

        var key = AuthKeys.ServiceSigner;
        if (!IssuerName().IsMatch(signer!.Issuer.Trim()))
        {
            return $"{key}:Issuer must be 1-100 characters of letters, digits, '.', '-' or '_'.";
        }
        if (string.IsNullOrWhiteSpace(signer.Audience) || signer.Audience.Trim().Length > 500)
        {
            return $"{key}:Audience must name the target service (1-500 characters).";
        }
        if (!Uri.TryCreate(signer.ServiceUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return $"{key}:ServiceUrl must be an absolute http(s) URL without credentials.";
        }
        try
        {
            using var _ = ImportPrivateKey(signer.PrivateKey);
        }
        catch (FormatException) { return $"{key}:PrivateKey must be a P-256 PKCS#8 private key in base64 or PEM."; }
        catch (CryptographicException) { return $"{key}:PrivateKey must be a P-256 PKCS#8 private key in base64 or PEM."; }
        catch (ArgumentException) { return $"{key}:PrivateKey must be a P-256 PKCS#8 private key in base64 or PEM."; }

        return null;
    }

    internal static ECDsa ImportPrivateKey(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new ArgumentException("Private key is empty.", nameof(encoded));
        }

        var trimmed = encoded.Trim();
        var key = ECDsa.Create();
        try
        {
            if (trimmed.StartsWith("-----BEGIN ", StringComparison.Ordinal))
            {
                key.ImportFromPem(trimmed);
            }
            else
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(trimmed), out _);
            }

            if (key.KeySize != 256)
            {
                throw new CryptographicException($"Expected P-256 key, got {key.KeySize}-bit key.");
            }

            if (!string.Equals(key.ExportParameters(false).Curve.Oid?.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
            {
                throw new CryptographicException("Expected P-256 (nistP256) key.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex IssuerName();
}

internal sealed class ServiceSignerValidation : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
        => ServiceAssertionSigner.Problem(options.ServiceSigner) is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
}
