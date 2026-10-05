using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Enforcement;
using DigitalBrain.Kernel.Enforcement;
using DigitalBrain.Types;

namespace DigitalBrain.Kernel.Platform.Secrets;

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
