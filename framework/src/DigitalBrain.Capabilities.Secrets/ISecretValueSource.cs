
namespace DigitalBrain.Capabilities.Secrets;

// Public SPI, implemented by a host's secrets store and read by the SecretRedactor: the store hands the redactor the current
// plaintext values of the calling brain's vault. Not a grain call surface, so the public ISecrets contract stays as it is.
public interface ISecretValueSource
{
    ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken);
}
