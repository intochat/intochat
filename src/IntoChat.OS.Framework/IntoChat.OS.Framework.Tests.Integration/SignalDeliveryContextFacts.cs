using IntoChat.OS.Testing;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Access;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class SignalDeliveryContextFacts(FrameworkHost host)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KernelDeliveryPreservesProvenanceAndMetadataWithoutImpersonatingThePublisher(bool fail)
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<IDeliverySource>($"source-{id}");
        var observer = brain.Get<IDeliveryObserver>($"subscriber/observer-{id}");
        await source.Watch(observer);
        var publisher = await host.PersonAsync("publisher", ct);
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
        var received = await TestWait.UntilAsync(_ => observer.Read(), seen => seen is not null, TimeSpan.FromSeconds(15), ct);
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
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<IDeliverySource>($"source-{id}");
        var observer = brain.Get<IDeliveryObserver>($"subscriber/observer-{id}");
        var publisher = await host.PersonAsync("publisher", ct);
        using (publisher.AsCaller())
        {
            var caller = CallerContextStamper.Require();
            Assert.True(await source.Emit(false));
            var forged = new SignalNudge(new(throughGrain ? "test-delivery-source/other" : source.GetGrainId().ToString()), 1);
            // Nudges are one-way: a refused one never reaches the observer, so the caller sees no error.
            if (throughGrain) { await source.Forward(observer, forged); }
            else { await observer.Nudge(forged); }
            Assert.Equal(caller, CallerContextStamper.Require());
        }
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
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
    Task Forward(INeuronObserver observer, SignalNudge nudge);
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
    public Task Forward(INeuronObserver observer, SignalNudge nudge) => observer.Nudge(nudge);
}

[GrainType("test-delivery-observer")]
public sealed class DeliveryObserver : Neuron, IDeliveryObserver
{
    private DeliverySeen? _seen;
    public Task<DeliverySeen?> Read() => Task.FromResult(_seen);
    private long _cursor;
    public async Task Nudge(SignalNudge nudge)
    {
        var callerPresent = CallerContextStamper.TryGet(out _);
        var metadata = RequestContext.Get("delivery-test") as string;
        (var signals, _cursor) = await NudgeReader.ReadAfter(NudgeReader.Source(GrainFactory, nudge), _cursor);
        if (signals is not [.., var signal]) { return; }
        _seen = new(callerPresent, metadata, signal.Publisher.Name);
        if (signal is DeliverySignal { Fail: true }) { throw new InvalidOperationException("Observer failed"); }
    }
}
