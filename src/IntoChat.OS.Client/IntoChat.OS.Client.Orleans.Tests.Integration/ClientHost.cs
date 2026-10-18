[assembly: AssemblyFixture(typeof(IntoChat.OS.Client.Orleans.Tests.Integration.ClientHost))]

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

// The shared host keeps default subscription options: a 500 ms operation timeout times out cold reader activations
// on a loaded CI runner. Facts that need fast leases or renewal start a FastBrain, and facts that count the client's
// tracked subscriptions, shrink the buffer or replace the journal keep their own TestBrain ([Trait("host", "own")]).
public sealed class ClientHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;
}

// Short leases and renewals for the facts that watch a subscription renew or outlive its lease.
internal static class FastBrain
{
    public static TestBrain Create(int capacity = 256) => TestBrain.Create().WithFastSubscriptions(capacity);

    public static Task<ITestBrain> StartAsync(CancellationToken ct) => Create().StartAsync(ct);
}
