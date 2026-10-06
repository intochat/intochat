using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Types;

namespace DigitalBrain.Framework.Platform.Secrets;

// Keep the existing Orleans identity so stored credentials remain addressable.
[Orleans.Metadata.DefaultGrainType("vault")]
[Alias("vault")]
[PlatformOnly]
public interface ISecrets : INeuron
{
    Task<SecretRef> Set(CallerContext caller, string name, string label, string value, CancellationToken cancellationToken = default);

    Task Remove(CallerContext caller, string name, CancellationToken cancellationToken = default);

    Task<string> Resolve(CallerContext caller, SecretRef secret, CancellationToken cancellationToken = default);
}
