using IntoChat.OS.Framework;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Secrets;

// The redactor's value source: the stamped caller's own vault, nobody else's.
// Zero-token auth enforcement: app/sandbox principals can never read raw vault secret values directly.
internal sealed class VaultSecretValueSource(AuthorityNeurons neurons, ICallerAmbient callers, EdgeAuthority? edge = null) : ISecretValueSource
{
    public async ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
    {
        if (callers.Current is not { } caller) { return []; }
        if (Identity.Grants.GrantRules.IsAppCaller(caller))
        {
            throw new UnauthorizedAccessException("Zero-token auth enforcement: sandbox and app principals cannot read raw vault secret values directly.");
        }
        // A person's own vault, or the vault of the person an agent acts for; the kernel, a module and the scheduler have none.
        if (caller.Actor is not (Actor.Person or Actor.Agent) || caller.For is not { } person) { return []; }
        if (edge is null) { return []; }
        return await edge.AsKernel(BrainId.Kernel, () => neurons.Vault(person.Principal).Values());
    }
}
