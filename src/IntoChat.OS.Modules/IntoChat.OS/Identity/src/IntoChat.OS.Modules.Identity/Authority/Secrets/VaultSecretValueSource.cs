using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Secrets;

// The redactor's value source: the stamped caller's own vault, nobody else's.
// Zero-token auth enforcement: app/sandbox principals can never read raw vault secret values directly.
internal sealed class VaultSecretValueSource(AuthorityNeurons neurons) : ISecretValueSource
{
    public async ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
    {
        if (!RuntimeCallerContext.TryGet(out var caller) || string.IsNullOrEmpty(caller.PrincipalId)) { return []; }
        if (caller.Kind == CallerKind.App || AppPrincipal.IsApp(caller.PrincipalId))
        {
            throw new UnauthorizedAccessException("Zero-token auth enforcement: sandbox and app principals cannot read raw vault secret values directly.");
        }
        return await neurons.Vault(caller.PrincipalId).Values(caller);
    }
}
