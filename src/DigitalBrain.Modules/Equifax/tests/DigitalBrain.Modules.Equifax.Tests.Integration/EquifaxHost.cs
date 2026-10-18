[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Equifax.Tests.Integration.EquifaxHost))]

namespace DigitalBrain.Modules.Equifax.Tests.Integration;

// The shared host composes Equifax with its deployment integration pointed at one fake bureau started for the
// assembly; facts on it only read reports. Facts that refuse tokens, count token or report calls, or drop the
// integration secret keep their own fake and TestBrain ([Trait("host", "own")]).
public sealed class EquifaxHost : BrainHostFixture
{
    private FakeEquifax? _fake;

    protected override TestBrain Configure(TestBrain brain)
        => EquifaxFacts.Compose(brain, _fake ?? throw new InvalidOperationException("The fake starts first."));

    public override async ValueTask InitializeAsync()
    {
        _fake = await FakeEquifax.StartAsync(TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fake is { } fake) { await fake.DisposeAsync(); }
    }
}
