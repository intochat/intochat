namespace DigitalBrain.Framework.Tests.Integration.Enforcement;

// A neuron keyed by the brain id itself (the workspace, the app index) belongs to that brain
// even though its key has no separator to classify by.
public sealed class BrainKeyedNeuronFacts(FrameworkHost host)
{
    [Fact]
    public async Task ANeuronKeyedByTheCallersBrainIsReachableAndAnotherBrainsIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("bob", ct);
        using var caller = alice.AsCaller();

        await brain.Get<ITestEmitter>(alice.BrainId).Emit(1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<ITestEmitter>(bob.BrainId).Emit(2));
    }
}
