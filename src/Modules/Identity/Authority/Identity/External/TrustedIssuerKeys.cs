using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity.External;

// The configured issuers' public keys, imported once. Configuration is validated on start (TrustedIssuersValidation),
// so a host with a malformed issuer refuses to start instead of refusing every ticket.
internal sealed partial class TrustedIssuerKeys : IDisposable
{
    private readonly Dictionary<string, (ECDsa Key, string Audience)> _issuers = new(StringComparer.Ordinal);

    public TrustedIssuerKeys(IOptions<AuthOptions> options)
    {
        foreach (var issuer in options.Value.TrustedIssuers)
        { _issuers[issuer.Name] = (ExternalSignInTicket.ImportPublicKey(issuer.PublicKey), issuer.Audience); }
    }

    public bool Any => _issuers.Count > 0;

    public (ECDsa Key, string Audience)? Of(string issuer) => _issuers.TryGetValue(issuer, out var trusted) ? trusted : null;

    public void Dispose()
    {
        foreach (var (key, _) in _issuers.Values) { key.Dispose(); }
    }

    // The first problem with the configured issuers, or null when they are all usable.
    internal static string? Problem(TrustedIssuer[]? issuers)
    {
        if (issuers is null) { return null; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < issuers.Length; index++)
        {
            var issuer = issuers[index];
            var key = AuthOptions.Key(nameof(AuthOptions.TrustedIssuers)) + ":" + index;
            if (issuer is null || !IssuerName().IsMatch(issuer.Name)) { return $"{key}:Name must be 1-100 characters of letters, digits, '.', '-' or '_'."; }
            if (!names.Add(issuer.Name)) { return $"{key}:Name '{issuer.Name}' is listed twice."; }
            if (string.IsNullOrWhiteSpace(issuer.Audience) || issuer.Audience.Length > 500) { return $"{key}:Audience must name this install (1-500 characters)."; }
            try { using var _ = ExternalSignInTicket.ImportPublicKey(issuer.PublicKey); }
            catch (FormatException) { return $"{key}:PublicKey must be a P-256 SubjectPublicKeyInfo in base64."; }
            catch (CryptographicException) { return $"{key}:PublicKey must be a P-256 SubjectPublicKeyInfo in base64."; }
            catch (ArgumentException) { return $"{key}:PublicKey must be a P-256 SubjectPublicKeyInfo in base64."; }
        }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex IssuerName();
}

internal sealed class TrustedIssuersValidation : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
        => TrustedIssuerKeys.Problem(options.TrustedIssuers) is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}
