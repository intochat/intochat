using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Framework.Platform.Secrets;

[Orleans.Metadata.DefaultGrainType("vault")]
[Alias("vault")]
public interface ISecrets : IGrainWithStringKey
{
    Task<SecretRef> Set(CallerContext caller, string name, string label, string value, CancellationToken cancellationToken = default);

    Task Remove(CallerContext caller, string name, CancellationToken cancellationToken = default);

    Task<string> Resolve(CallerContext caller, SecretRef secret, CancellationToken cancellationToken = default);
}
