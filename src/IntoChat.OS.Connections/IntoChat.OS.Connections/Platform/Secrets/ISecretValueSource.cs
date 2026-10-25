using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Secrets;

// Public SPI, implemented by IntoChat.OS.Authority's secrets store, read by the SecretRedactor: the store hands the redactor the current plaintext values of the
// calling brain's vault. Not a grain call surface, so the public ISecrets contract stays as it is.
public interface ISecretValueSource
{
    ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken);
}
