using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Identity;

namespace IntoChat.OS.Kernel.Tests.Integration.Capabilities;

// 05-12, CAP-01: the Authority directory passes the identity@1 kit through the runtime's IdentityCapability and GrantsCapability
// (AuthorityDirectoryPort over IIdentityClient), resolved through the broker. Sessions and description are Phase 9's (the kit
// skips them without a session port).
[Trait("host", "own")]
public sealed class IdentityCapabilityConformance(CapabilityHost host) : IdentityConformance<IdentityCapability>, IClassFixture<CapabilityHost>
{
    protected override async Task<IdentitySubject> Connect(CancellationToken ct)
        => new(await host.Broker.Resolve<IIdentity>(CapabilityHost.KitBrain, ct), await host.Broker.Resolve<IGrants>(CapabilityHost.KitBrain, ct), new RuntimeCallerScope());
}
