using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

// R53: at silo start a machine registers for the account it serves — the Open posture's default owner or the
// Secured bootstrap user — without waiting for anyone to sign in.
internal sealed class MachineRegistration(MachineRegistrar registrar, IOptions<AuthOptions> auth) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken)
    {
        var account = auth.Value.Posture == IdentityPosture.Open ? AccountSession.DefaultLogin : auth.Value.Username;
        return string.IsNullOrWhiteSpace(account) ? Task.CompletedTask : registrar.Bind(account);
    }
}
