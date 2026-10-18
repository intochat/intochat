[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Monnet.Tests.Integration.MonnetHost))]

namespace DigitalBrain.Modules.Monnet.Tests.Integration;

// The shared host composes Monnet (with its webhook secret) over one fake Monnet started for the assembly; facts on it
// connect and receive webhooks in a fresh person's brain and never read the fake's request log. Facts that refuse keys,
// count requests or drop the webhook secret keep their own fake and TestBrain ([Trait("host", "own")]).
public sealed class MonnetHost : BrainHostFixture
{
    private FakeMonnet? _fake;

    internal FakeMonnet Fake => _fake ?? throw new InvalidOperationException("The fake starts in InitializeAsync.");

    protected override TestBrain Configure(TestBrain brain) => MonnetBrain.Compose(brain, Fake);

    public override async ValueTask InitializeAsync()
    {
        _fake = await FakeMonnet.StartAsync(TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fake is { } fake) { await fake.DisposeAsync(); }
    }
}
