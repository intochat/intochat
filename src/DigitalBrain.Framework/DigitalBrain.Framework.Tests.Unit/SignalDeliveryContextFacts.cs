namespace DigitalBrain.Framework.Tests.Unit;

public sealed class SignalDeliveryContextFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KernelDeliveryPreservesProvenanceAndMetadataWithoutImpersonatingThePublisher(bool fail)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var source = brain.Get<IDeliverySource>("source");
        var observer = brain.Get<IDeliveryObserver>("subscriber/observer");
        await source.Watch(observer);
        var publisher = await brain.PersonAsync("publisher", ct);
        using (publisher.AsCaller())
        {
            var caller = CallerContextStamper.Require();
            RequestContext.Set("delivery-test", "retained");
            Assert.True(await source.Emit(fail));
            Assert.Equal(caller, CallerContextStamper.Require());
            // Delivery does not exempt ordinary calls, even immediately afterwards.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(observer.Read);
            RequestContext.Remove("delivery-test");
        }
        var received = await observer.Read();
        Assert.NotNull(received);
        Assert.False(received.CallerPresent);
        Assert.Equal("retained", received.Metadata);
        Assert.Equal(source.GetGrainId().ToString(), received.Publisher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientAndMismatchedPublisherCallbacksDoNotReceiveInternalAuthority(bool throughGrain)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var source = brain.Get<IDeliverySource>("source");
        var observer = brain.Get<IDeliveryObserver>("subscriber/observer");
        var publisher = await brain.PersonAsync("publisher", ct);
        using (publisher.AsCaller())
        {
            var caller = CallerContextStamper.Require();
            var forged = new DeliverySignal(false) { Publisher = new(throughGrain ? "test-delivery-source/other" : source.GetGrainId().ToString()) };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => throughGrain
                ? source.Forward(observer, forged)
                : observer.OnSignalAsync(forged));
            Assert.Equal(caller, CallerContextStamper.Require());
        }
        Assert.Null(await observer.Read());
    }
}

[GenerateSerializer] public sealed record DeliverySignal([property: Id(0)] bool Fail) : Signal;
[GenerateSerializer]
public sealed record DeliverySeen(
    [property: Id(0)] bool CallerPresent, [property: Id(1)] string? Metadata, [property: Id(2)] string Publisher);
[Alias("test.delivery-source")]
[Public]
public interface IDeliverySource : INeuron
{
    Task<bool> Emit(bool fail);
    Task Forward(INeuronObserver observer, Signal signal);
}
[Alias("test.delivery-observer")]
public interface IDeliveryObserver : INeuron, INeuronObserver { Task<DeliverySeen?> Read(); }

[GrainType("test-delivery-source")]
public sealed class DeliverySource : Neuron, IDeliverySource
{
    public async Task<bool> Emit(bool fail)
    {
        var caller = CallerContextStamper.Require();
        await PublishAsync(new DeliverySignal(fail));
        return caller == CallerContextStamper.Require();
    }
    public Task Forward(INeuronObserver observer, Signal signal) => observer.OnSignalAsync(signal);
}

[GrainType("test-delivery-observer")]
public sealed class DeliveryObserver : Neuron, IDeliveryObserver
{
    private DeliverySeen? _seen;
    public Task<DeliverySeen?> Read() => Task.FromResult(_seen);
    public Task OnSignalAsync(Signal signal)
    {
        _seen = new(CallerContextStamper.TryGet(out _), RequestContext.Get("delivery-test") as string, signal.Publisher.Name);
        if (signal is DeliverySignal { Fail: true }) { throw new InvalidOperationException("Observer failed"); }
        return Task.CompletedTask;
    }
}
