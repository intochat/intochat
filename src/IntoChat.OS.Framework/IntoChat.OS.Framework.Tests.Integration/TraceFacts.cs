using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class TraceFacts(FrameworkHost host)
{
    [Fact]
    public async Task APublishedSignalCarriesTheCurrentTrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<ITraceSource>($"source-{id}");
        var observer = brain.Get<ITraceObserver>($"subscriber/observer-{id}");
        await source.Watch(observer);
        var trace = TraceContext.Begin("acct-1", "brain-1", new NeuronId("brain-1/button"));
        using var _ = TraceContext.Enter(trace);
        await source.Emit();
        var signal = await Seen(observer, ct);
        Assert.NotNull(signal);
        Assert.Equal(trace.TraceId, signal.DeliveryTraceId);
        Assert.Equal("acct-1", signal.DeliveryPayer);
    }

    [Fact]
    public async Task AUserSetTraceIsOverwrittenAtPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<ITraceSource>($"source-{id}");
        var observer = brain.Get<ITraceObserver>($"subscriber/observer-{id}");
        await source.Watch(observer);
        var forged = new Trace { TraceId = "forged", PayerAccountId = "mallory", BrainId = "mallory", Origin = new NeuronId("mallory/edge") };
        await source.Emit(forged);
        var signal = await Seen(observer, ct);
        Assert.NotNull(signal);
        Assert.Null(signal.DeliveryTraceId);
    }

    [Fact]
    public async Task KernelDeliveryKeepsTheTraceWhileStrippingTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<ITraceSource>($"source-{id}");
        var observer = brain.Get<ITraceObserver>($"subscriber/observer-{id}");
        await source.Watch(observer);
        var publisher = await host.PersonAsync("publisher", ct);
        Trace trace;
        using (publisher.AsCaller())
        {
            var caller = CallerContextStamper.Require();
            trace = TraceContext.Current!;
            await source.Emit();
            Assert.Equal(caller, CallerContextStamper.Require());
        }
        var seen = await Seen(observer, ct);
        Assert.False(seen!.CallerPresent);
        Assert.Equal(trace.TraceId, seen.DeliveryTraceId);
        Assert.Equal(source.GetGrainId().ToString(), seen.Publisher);
    }

    [Fact]
    public async Task AReactionInAnotherBrainKeepsTheOriginalPayer()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<ITraceSource>("publisher/button");
        var observer = brain.Get<ITraceObserver>("reacting/subscriber");
        await source.Watch(observer);
        var trace = TraceContext.Begin("acct-1", "brain-1", new NeuronId("brain-1/button"));
        using var _ = TraceContext.Enter(trace);
        await source.Emit();
        var seen = await Seen(observer, ct);
        Assert.NotNull(seen);
        Assert.Equal("acct-1", seen.DeliveryPayer);
        Assert.Equal("brain-1", seen.DeliveryBrain);
        Assert.Equal(trace.TraceId, seen.DeliveryTraceId);
    }

    // Nudges are one-way: the observer has read the signal once it has seen something.
    private static async Task<TraceSeen?> Seen(ITraceObserver observer, CancellationToken ct)
        => await TestWait.UntilAsync(_ => observer.Read(), seen => seen is not null, TimeSpan.FromSeconds(15), ct);
}

[GenerateSerializer] public sealed record TraceSignal : Signal;
[GenerateSerializer]
public sealed record TraceSeen(
    [property: Id(0)] bool CallerPresent,
    [property: Id(1)] string? DeliveryTraceId,
    [property: Id(2)] string? DeliveryPayer,
    [property: Id(3)] string? DeliveryBrain,
    [property: Id(4)] string Publisher);
[Alias("test.trace-source")]
[Public]
public interface ITraceSource : INeuron
{
    Task Emit(Trace? forged = null);
}
[Alias("test.trace-observer")]
public interface ITraceObserver : INeuron, INeuronObserver { Task<TraceSeen?> Read(); }

[GrainType("test-trace-source")]
public sealed class TraceSource : Neuron, ITraceSource
{
    public Task Emit(Trace? forged = null)
    {
        var signal = forged is null ? new TraceSignal() : new TraceSignal() with { Trace = forged };
        return PublishAsync(signal);
    }
}

[GrainType("test-trace-observer")]
public sealed class TraceObserver : Neuron, ITraceObserver
{
    private TraceSeen? _seen;
    public Task<TraceSeen?> Read() => Task.FromResult(_seen);
    private long _cursor;
    public async Task Nudge(SignalNudge nudge)
    {
        var callerPresent = CallerContextStamper.TryGet(out _);
        (var signals, _cursor) = await NudgeReader.ReadAfter(NudgeReader.Source(GrainFactory, nudge), _cursor);
        if (signals is [.., var signal])
        { _seen = new TraceSeen(callerPresent, signal.Trace?.TraceId, signal.Trace?.PayerAccountId, signal.Trace?.BrainId, signal.Publisher.Name); }
    }
}
