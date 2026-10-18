using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Authority.Hosted;

internal sealed class RuntimeMachineAuthentication(IOptions<HostedRuntimeOptions> options, TimeProvider clock)
{
    public async Task<bool> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        var configuration = options.Value;
        if (!configuration.IsConfigured || !context.Request.IsHttps) { return false; }
        var certificate = await context.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) { return false; }
        var now = clock.GetUtcNow().UtcDateTime;
        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime()) { return false; }
        return CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256),
            Convert.FromHexString(configuration.ControllerCertificateSha256));
    }
}
