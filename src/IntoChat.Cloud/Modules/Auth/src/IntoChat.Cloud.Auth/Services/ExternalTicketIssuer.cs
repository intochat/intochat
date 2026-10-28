using System.Security.Cryptography;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace IntoChat.Cloud.Auth.Services;

// Hands a signed-in person to an OS install that trusts this cloud: an ES256 external sign-in ticket for the install's
// public origin, redeemed once by the install at /identity/external. A return URL on any other origin is left alone.
internal sealed class ExternalTicketIssuer(AuthSecrets secrets, AuthModuleOptions options, TimeProvider clock) : IDisposable
{
    // Installs scope the principals this issuer signs in by its name, so it never changes.
    public const string IssuerName = "intochat-cloud";
    public const string Integration = "signin-issuer";
    public const string SigningKeySecret = "SigningKey";
    public const string RedeemPath = "/identity/external";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(ExternalSignInTicket.LifetimeSeconds);

    private readonly Lock gate = new();
    private ECDsa? key;

    // The install origin a return URL points at, when it is one of the configured OS installs.
    public string? InstallOf(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || !Uri.TryCreate(returnUrl, UriKind.Absolute, out var target)) { return null; }
        var origin = target.GetLeftPart(UriPartial.Authority);
        return options.OsInstalls.Select(Origin).FirstOrDefault(install => string.Equals(install, origin, StringComparison.OrdinalIgnoreCase));
    }

    // The URL that signs the person in on the install, or null when the return URL does not target an OS install.
    public async Task<string?> RedirectFor(string? returnUrl, string email, string? name, string amr)
    {
        if (InstallOf(returnUrl) is not { } install) { return null; }
        var signer = await SigningKey();
        var claims = new ExternalSignInClaims(IssuerName, install, email, Trimmed(name, email), EmailVerified: true, Method(amr),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), clock.GetUtcNow().Add(Lifetime));
        var ticket = ExternalSignInTicket.Sign(claims, signer, clock);
        var path = new Uri(returnUrl!).PathAndQuery;
        return QueryHelpers.AddQueryString(install + RedeemPath, new Dictionary<string, string?> { ["t"] = ticket, ["returnUrl"] = path });
    }

    // A JSON sign-in flow carries its destination as ?returnUrl=; one outside AllowedReturnOrigins is ignored.
    public Task<string?> RedirectFor(HttpRequest request, string email, string? name, string amr)
    {
        var returnUrl = request.Query["returnUrl"].FirstOrDefault();
        return returnUrl is not null && new ReturnUrlValidator(options.AllowedReturnOrigins).IsAllowed(returnUrl)
            ? RedirectFor(returnUrl, email, name, amr)
            : Task.FromResult<string?>(null);
    }

    // The JSON body of a flow that signed the person in; redirect is present only for an OS install.
    public static IResult SignedIn(Member member, string? redirect)
    {
        var body = new Dictionary<string, object?> { ["principalId"] = member.PrincipalId, ["accountId"] = member.AccountId, ["brainId"] = member.BrainId };
        if (redirect is not null) { body["redirect"] = redirect; }
        return Results.Json(body);
    }

    // Auth's amr ("pwd otp_email", "google", "email otp_email", ...) as the ticket's single method.
    public static string Method(string amr)
    {
        var parts = amr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains("google") ? "google" : parts.FirstOrDefault() == "pwd" ? "pwd" : "otp";
    }

    public void Dispose() => key?.Dispose();

    private static string Origin(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "";

    private static string Trimmed(string? name, string email)
    {
        var value = string.IsNullOrWhiteSpace(name) ? email : name.Trim();
        return value.Length <= 200 ? value : value[..200];
    }

    private async Task<ECDsa> SigningKey()
    {
        if (key is { } loaded) { return loaded; }
        var value = await Task.Run(() => secrets.Named(AuthSecrets.SigningKey));
        lock (gate)
        {
            if (key is not null) { return key; }
            key = Import(value);
            return key;
        }
    }

    // PKCS#8 DER in base64, P-256 only. The message names the setting, never the value.
    internal static ECDsa Import(string? pkcs8Base64)
    {
        var imported = ECDsa.Create();
        try
        {
            imported.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64 ?? ""), out _);
            if (imported.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") { throw new CryptographicException(); }
            return imported;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            imported.Dispose();
            throw new InvalidOperationException(
                "Auth sign-in signing key is not configured: provide the deployment secret signin-issuer:SigningKey as a P-256 PKCS#8 key in base64.");
        }
    }
}
