using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Secrets;

namespace IntoChat.OS.Authority.Secrets;

// Only this assembly can name the grain interface. The vault is keyed by the calling principal.
[Orleans.Metadata.DefaultGrainType("vault")]
[Alias("vault-values")]
internal interface ISecretVaultValues : IGrainWithStringKey
{
    Task<string[]> Values();
}

// The redactor's value source: the stamped caller's own vault, nobody else's.
internal sealed class VaultSecretValueSource(AuthorityNeurons neurons) : ISecretValueSource
{
    public async ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
    {
        if (!CallerContextStamper.TryGet(out var caller) || string.IsNullOrEmpty(caller.PrincipalId)) { return []; }
        return await neurons.SecretValues(caller.PrincipalId).Values();
    }
}
