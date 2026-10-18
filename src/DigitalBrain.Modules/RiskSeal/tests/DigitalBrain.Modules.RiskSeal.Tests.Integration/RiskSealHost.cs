[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.RiskSeal.Tests.Integration.RiskSealHost))]

namespace DigitalBrain.Modules.RiskSeal.Tests.Integration;

// The shared host composes RiskSeal with its deployment key over one fake RiskSeal started for the assembly; facts on
// it score or connect in a fresh person's brain and never read the fake's request log. Facts that change the valid key,
// count bearers or bodies, or drop the deployment key keep their own fake and TestBrain ([Trait("host", "own")]).
public sealed class RiskSealHost : BrainHostFixture
{
    private FakeRiskSeal? _fake;

    protected override TestBrain Configure(TestBrain brain)
        => RiskSealFacts.Compose(brain, _fake ?? throw new InvalidOperationException("The fake starts first."));

    public override async ValueTask InitializeAsync()
    {
        _fake = await FakeRiskSeal.StartAsync(TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fake is { } fake) { await fake.DisposeAsync(); }
    }
}
