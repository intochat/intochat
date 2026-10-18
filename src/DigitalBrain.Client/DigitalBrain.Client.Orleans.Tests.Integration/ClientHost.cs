[assembly: AssemblyFixture(typeof(DigitalBrain.Client.Orleans.Tests.Integration.ClientHost))]

namespace DigitalBrain.Client.Orleans.Tests.Integration;

// The shared host with fast subscription leases, which the lifetime facts need and the rest tolerate. Facts that count
// the client's tracked subscriptions, shrink the buffer or replace the journal keep their own TestBrain
// ([Trait("host", "own")]).
public sealed class ClientHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithFastSubscriptions();
}
