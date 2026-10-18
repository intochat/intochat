[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Twilio.Tests.Integration.TwilioHost))]

namespace DigitalBrain.Modules.Twilio.Tests.Integration;

// The shared host composes Twilio with its deployment account over one fake Twilio started for the assembly; the
// status-callback facts send and receive callbacks in a fresh person's brain and never read the fake's log. Facts that
// set the fake's answer or read its sends and requests keep their own fake and TestBrain ([Trait("host", "own")]).
public sealed class TwilioHost : BrainHostFixture
{
    private FakeTwilio? _fake;

    internal FakeTwilio Fake => _fake ?? throw new InvalidOperationException("The fake starts in InitializeAsync.");

    protected override TestBrain Configure(TestBrain brain) => TwilioFacts.Compose(brain, Fake);

    public override async ValueTask InitializeAsync()
    {
        _fake = await FakeTwilio.StartAsync(TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fake is { } fake) { await fake.DisposeAsync(); }
    }
}
