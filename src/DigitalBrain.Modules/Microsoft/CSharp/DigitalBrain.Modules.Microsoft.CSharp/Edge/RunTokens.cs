using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Microsoft.CSharp.Edge;

internal sealed record RunTokenClaims(string File, string Run, DateTimeOffset Expires);

// A run's bearer token for the script edge: the file and run it speaks for, signed with HMAC-SHA256.
// Development signs with a key made at startup; production must configure one shared by every silo.
internal sealed class RunTokens(IModuleIntegrations<CSharpModule> integrations, TimeProvider time, IHostEnvironment environment, ILogger<RunTokens> logger) : IIntegrationStartup
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

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
            logger.LogWarning("The csharp Integration is unprovisioned; generated development run-token key. Tokens will not survive restart.");
        }
        else { throw new InvalidOperationException("The csharp Integration RunTokenKey must be provisioned before production startup."); }
    }

    private byte[] Key => _key ?? throw new InvalidOperationException("Run-token credentials have not completed Integration startup.");

    public string Issue(string file, string run)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new RunTokenClaims(file, run, time.GetUtcNow() + Lifetime), JsonSerializerOptions.Web);
        return Base64Url(claims) + "." + Base64Url(HMACSHA256.HashData(Key, claims));
    }

    public RunTokenClaims? Validate(string? token)
    {
        if (token?.Split('.') is not [var payload, var signature]) { return null; }
        byte[] claims;
        try { claims = FromBase64Url(payload); }
        catch (FormatException) { return null; }
        byte[] presented;
        try { presented = FromBase64Url(signature); }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key, claims), presented)) { return null; }
        var parsed = JsonSerializer.Deserialize<RunTokenClaims>(claims, JsonSerializerOptions.Web);
        return parsed is not null && parsed.Expires > time.GetUtcNow() ? parsed : null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
