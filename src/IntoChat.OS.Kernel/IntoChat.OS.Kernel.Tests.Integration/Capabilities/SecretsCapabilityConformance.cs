using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Secrets;

namespace IntoChat.OS.Kernel.Tests.Integration.Capabilities;

// 05-12, CAP-01: the Authority vault passes the secrets@1 kit through the runtime's SecretsCapability (AuthorityVaultPort),
// resolved through the broker as the kernel registers it.
[Trait("host", "own")]
public sealed class SecretsCapabilityConformance(CapabilityHost host) : SecretsConformance<SecretsCapability>, IClassFixture<CapabilityHost>
{
    protected override async Task<SecretsSubject> Connect(CancellationToken ct)
        => new(await host.Broker.Resolve<ISecrets>(CapabilityHost.KitBrain, ct), new RuntimeCallerScope(), () => host.Logs.Lines);
}
