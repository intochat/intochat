using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Runtime.InMemory.Tests.Unit;

// Neuron.Defer sends a one-way call to the neuron's own reference and refuses any other call before sending it.
public sealed class DeferFacts
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AOneWayTargetIsDeliveredBehindTheTurn()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var worker = runtime.ResolveKey<IWorker>("brain-1/defer-one-way");

        await worker.DeferPost(7).WaitAsync(_ct);
        await runtime.Idle().WaitAsync(_ct);

        Assert.Equal(7, await worker.Count().WaitAsync(_ct));
    }

    [Fact]
    public async Task ATargetThatIsNotOneWayThrowsBeforeSending()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var worker = runtime.ResolveKey<IWorker>("brain-1/defer-two-way");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.DeferCount().WaitAsync(_ct));

        Assert.Contains("must be [OneWay]", refused.Message, StringComparison.Ordinal);
    }
}
