using System.Security.Cryptography;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Identity.Directory;

// Registers each seeded account once. An account that already exists is left as it is: a seed
// never resets a password someone may have changed.
internal sealed class SeededAccountsStartup(IGrainFactory grains, IOptions<SeededAccountOptions> options, ILogger<SeededAccountsStartup> logger) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        foreach (var seed in options.Value.Accounts)
        {
            var principal = grains.GetGrain<IPrincipal>(seed.Username);
            if (await principal.DefaultMembership().WaitAsync(cancellationToken) is not null)
            {
                if (seed.Password is { Length: > 0 } password && await principal.Authenticate(password).WaitAsync(cancellationToken) is null)
                { logger.LogWarning("Seeded account {Username} already exists with another password; it keeps that password.", seed.Username); }
                continue;
            }
            // A password-less seed gets an unguessable one nobody holds.
            var secret = seed.Password is { Length: > 0 } given ? given : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await principal.Register(secret, seed.DisplayName.Length > 0 ? seed.DisplayName : seed.Username).WaitAsync(cancellationToken);
        }
    }
}
