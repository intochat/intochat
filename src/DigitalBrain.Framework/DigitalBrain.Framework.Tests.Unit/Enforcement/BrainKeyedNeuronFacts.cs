using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Framework.Tests.Unit.Enforcement;

// A neuron keyed by the brain id itself (the workspace, the app index) belongs to that brain
// even though its key has no separator to classify by.
public sealed class BrainKeyedNeuronFacts
{
    [Fact]
    public async Task ANeuronKeyedByTheCallersBrainIsReachableAndAnotherBrainsIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "one",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            await host.AuthorizeCallerAsync();
            await host.Get<ITestEmitter>("one").Emit(1);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Get<ITestEmitter>("two").Emit(2));
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }
}
