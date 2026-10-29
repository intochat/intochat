using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Secrets;

namespace IntoChat.OS.Authority.Secrets;

// The redactor's value source: the stamped caller's own vault, nobody else's.
internal sealed class VaultSecretValueSource(AuthorityNeurons neurons) : ISecretValueSource
{
    public async ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
    {
        if (!CallerContextStamper.TryGet(out var caller) || string.IsNullOrEmpty(caller.PrincipalId)) { return []; }
        return await neurons.Vault(caller.PrincipalId).Values(caller);
    }
}
