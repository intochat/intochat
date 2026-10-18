[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Salesforce.Tests.Integration.SalesforceHost))]

namespace DigitalBrain.Modules.Salesforce.Tests.Integration;

// The shared host composes Salesforce over one fake org and one shifted clock for the assembly; facts on it act in a
// fresh person's brain and neither script the fake nor read its log. Facts that set grants or results, refuse refresh,
// take the fake down, move the clock, or count requests and tokens keep their own fake and TestBrain
// ([Trait("host", "own")]).
public sealed class SalesforceHost : BrainHostFixture
{
    private FakeSalesforce? _fake;

    internal FakeSalesforce Fake => _fake ?? throw new InvalidOperationException("The fake starts in InitializeAsync.");

    internal ShiftedClock Clock { get; } = new();

    protected override TestBrain Configure(TestBrain brain) => SalesforceFacts.Compose(brain, Fake, Clock);

    public override async ValueTask InitializeAsync()
    {
        _fake = await FakeSalesforce.StartAsync(TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fake is { } fake) { await fake.DisposeAsync(); }
    }
}
