using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Sessions;

internal sealed record SessionClaims(string Brain, string Session, DateTimeOffset Expires);

// A sandbox session's bearer token for the runtime edge: the brain and session it speaks for, signed
// with HMAC-SHA256. The brain rotates it over the stream before it expires.
// Development signs with a key made at startup; production must configure one shared by every silo.
internal sealed class SessionTokens(IModuleIntegrations<CSharpModule> integrations, TimeProvider time, IHostEnvironment environment, ILogger<SessionTokens> logger) : IIntegrationStartup
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private byte[]? _key;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var handle = integrations.Get("csharp");
        var status = await handle.ReadAsync(cancellationToken);
        if (status.Status == IntegrationStatus.Ready)
        {
            var key = Convert.FromBase64String(await handle.ReadSecretAsync("RunTokenKey", cancellationToken));
            if (key.Length < 32) { throw new InvalidOperationException("The csharp Integration RunTokenKey must contain at least 32 bytes."); }
            _key = key;
        }
        else if (environment.IsDevelopment())
        {
            _key = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning("The csharp Integration is unprovisioned; generated development session-token key. Tokens will not survive restart.");
        }
        else { throw new InvalidOperationException("The csharp Integration RunTokenKey must be provisioned before production startup."); }
    }

    private byte[] Key => _key ?? throw new InvalidOperationException("Session-token credentials have not completed Integration startup.");

    public string Issue(string brain, string session)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new SessionClaims(brain, session, time.GetUtcNow() + Lifetime), JsonSerializerOptions.Web);
        return Base64Url(claims) + "." + Base64Url(HMACSHA256.HashData(Key, claims));
    }

    public SessionClaims? Validate(string? token)
    {
        if (token?.Split('.') is not [var payload, var signature]) { return null; }
        byte[] claims;
        try { claims = FromBase64Url(payload); }
        catch (FormatException) { return null; }
        byte[] presented;
        try { presented = FromBase64Url(signature); }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key, claims), presented)) { return null; }
        var parsed = JsonSerializer.Deserialize<SessionClaims>(claims, JsonSerializerOptions.Web);
        return parsed is not null && parsed.Expires > time.GetUtcNow() ? parsed : null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
