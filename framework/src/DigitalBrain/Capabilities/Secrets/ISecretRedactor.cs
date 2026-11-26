
namespace DigitalBrain.Capabilities.Secrets;

// Masks the calling brain's vault values in text before it is announced, stored or exported.
public interface ISecretRedactor
{
    ValueTask<string?> Redact(string? text, CancellationToken cancellationToken);

    // How many trailing characters of a stream may still become part of a secret: the longest
    // maskable value minus one. A streamed text holds that tail back until it is complete.
    ValueTask<int> TailLength(CancellationToken cancellationToken);
}
