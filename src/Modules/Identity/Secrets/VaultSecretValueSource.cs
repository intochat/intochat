using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Host.Callers;

namespace IntoChat.OS.Modules.Identity.Secrets;

// The redactor's value source: the stamped caller's own vault, nobody else's.
// Zero-token auth enforcement: app/sandbox principals can never read raw vault secret values directly.
internal sealed class VaultSecretValueSource(AuthorityNeurons neurons, ICallerAmbient callers, EdgeAuthority? edge = null) : ISecretValueSource
{
    public async ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
    {
        if (callers.Current is not { } caller) { return []; }
        if (IntoChat.OS.Modules.Identity.Grants.GrantRules.IsAppCaller(caller))
        {
            throw new UnauthorizedAccessException("Zero-token auth enforcement: sandbox and app principals cannot read raw vault secret values directly.");
        }
        if (VaultOwner(caller) is not { } owner) { return []; }
        if (edge is null) { return []; }
        return await edge.AsKernel(BrainId.Kernel, () => neurons.Vault(owner).Values());
    }

    // Whose vault values are redacted, from the stamp: a person's own, the person an agent acts for, a module's own id, or
    // the kernel's brain. The scheduler has none.
    internal static string? VaultOwner(Caller caller) => caller.Actor switch
    {
        Actor.Person or Actor.Agent => caller.For?.Principal,
        Actor.Module module => module.Id,
        Actor.Kernel => caller.Brain.Value,
        _ => null,
    };
}
