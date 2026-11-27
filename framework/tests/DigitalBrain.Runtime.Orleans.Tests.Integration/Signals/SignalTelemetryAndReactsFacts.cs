using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;
using DigitalBrain.Runtime.Orleans.Tests.Integration.Synapses;
using DigitalBrain.Signals;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Signals;

/// <summary>
/// Proves first-class <c>DigitalBrain.Signals</c> spans and declarative <c>[Reacts]</c>
/// lazy-activation wiring end-to-end on the Orleans runtime (<c>BrainLog</c> + <c>GrainNudge</c>).
/// </summary>
public sealed class SignalTelemetryAndReactsFacts(SynapseSilo silo) : IClassFixture<SynapseSilo>
{
    private const string SecretPayload = "ORLEANS_SECRET_PAYLOAD_DO_NOT_LEAK_42";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeclarativeReacts_LazyActivatesNeverActivatedNeuronAndEmitsSafeSignalSpans()
    {
        var captured = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SignalTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        using var platform = TestStamp.Kernel();
        const string Brain = "b-os09-orleans-1";
        const string Source = $"{Brain}/probe.os09.orleans.source/s1";
        const string OtherSource = $"{Brain}/probe.os09.orleans.other/o1";
        const string Reactor = $"{Brain}/probe.os09.orleans.reactor/default";

        // Non-matching source must not deliver to [Reacts(typeof(IOrleansPingSource), ...)].
        await silo.Get<IOrleansOtherPingSource>(OtherSource).Emit(99, "ignored");

        // Matching source lazy-activates the never-activated reactor neuron via BrainLog + GrainNudge.
        await silo.Get<IOrleansPingSource>(Source).Emit(1, SecretPayload);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        int[] received;
        while ((received = await silo.Get<IOrleansPingReactor>(Reactor).Received()).Length < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }
        Assert.Equal([1], received);

        // Wait briefly for downstream publish span of OrleansPingAck to complete.
        var spanDeadline = DateTime.UtcNow.AddSeconds(10);
        Activity[] activities;
        while (true)
        {
            activities = captured
                .Where(a => string.Equals(a.GetTagItem(SignalTelemetry.PublisherTag) as string, Source, StringComparison.Ordinal)
                         || string.Equals(a.GetTagItem(SignalTelemetry.PublisherTag) as string, Reactor, StringComparison.Ordinal))
                .ToArray();
            if (activities.Any(a => a.OperationName == SignalTelemetry.PublishSpanName
                                 && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.os09.orleans.ack", StringComparison.Ordinal))
                || DateTime.UtcNow >= spanDeadline)
            {
                break;
            }
            await Task.Delay(25, Ct);
        }

        var publishPing = Assert.Single(activities, a =>
            a.OperationName == SignalTelemetry.PublishSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.os09.orleans.ping", StringComparison.Ordinal));
        Assert.Equal(ActivityKind.Producer, publishPing.Kind);
        Assert.Equal(Source, publishPing.GetTagItem(SignalTelemetry.PublisherTag));
        Assert.Equal(1L, publishPing.GetTagItem(SignalTelemetry.SequenceTag));

        var reactPing = Assert.Single(activities, a =>
            a.OperationName == SignalTelemetry.ReactSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.os09.orleans.ping", StringComparison.Ordinal));
        Assert.Equal(ActivityKind.Consumer, reactPing.Kind);
        Assert.Equal(Source, reactPing.GetTagItem(SignalTelemetry.PublisherTag));
        Assert.Equal(Reactor, reactPing.GetTagItem(SignalTelemetry.SubscriberTag));
        // Brain-wide subscriptions carry the brain position (2, after OtherSource emitted at position 1).
        var reactSeq = Assert.IsType<long>(reactPing.GetTagItem(SignalTelemetry.SequenceTag));
        Assert.Equal(2L, reactSeq);
        Assert.Equal(Causation.Address(NeuronId.Parse(Source), reactSeq), reactPing.GetTagItem(SignalTelemetry.CausationIdTag));

        var publishAck = Assert.Single(activities, a =>
            a.OperationName == SignalTelemetry.PublishSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.os09.orleans.ack", StringComparison.Ordinal));
        Assert.Equal(Reactor, publishAck.GetTagItem(SignalTelemetry.PublisherTag));
        Assert.Equal(1L, publishAck.GetTagItem(SignalTelemetry.SequenceTag));
        Assert.Equal(Causation.Address(NeuronId.Parse(Source), reactSeq), publishAck.GetTagItem(SignalTelemetry.CausationIdTag));

        // Strict PII/payload safety check across all emitted spans.
        foreach (var activity in activities)
        {
            Assert.DoesNotContain(SecretPayload, activity.DisplayName, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretPayload, activity.StatusDescription ?? string.Empty, StringComparison.Ordinal);
            foreach (var tag in activity.TagObjects)
            {
                Assert.DoesNotContain(SecretPayload, tag.Key, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretPayload, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task DeclarativeReacts_PersistsCursorAcrossDeactivationAndDoesNotSelfLoop()
    {
        using var platform = TestStamp.Kernel();
        const string Brain = "b-os09-orleans-2";
        const string Source = $"{Brain}/probe.os09.orleans.source/s2";
        const string Reactor = $"{Brain}/probe.os09.orleans.reactor/default";

        await silo.Get<IOrleansPingSource>(Source).Emit(10, "first");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        int[] received;
        while ((received = await silo.Get<IOrleansPingReactor>(Reactor).Received()).Length < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }
        Assert.Equal([10], received);

        // Force grain deactivation, then emit a second signal: reactor must re-activate without replaying 10.
        await silo.DeactivateAll();
        await silo.Get<IOrleansPingSource>(Source).Emit(20, "second");

        deadline = DateTime.UtcNow.AddSeconds(20);
        while ((received = await silo.Get<IOrleansPingReactor>(Reactor).Received()).Length < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }
        Assert.Equal([10, 20], received);
    }
}

[Signal("probe.os09.orleans.ping")]
public sealed record OrleansPing(int Value, string SecretBody) : Signal;

[Signal("probe.os09.orleans.ack")]
public sealed record OrleansPingAck(int Value, string SecretBody) : Signal;

public interface IOrleansPingSource
{
    Task Emit(int value, string secretBody);
}

public interface IOrleansOtherPingSource
{
    Task Emit(int value, string secretBody);
}

public interface IOrleansPingReactor
{
    Task<int[]> Received();
}

public sealed record OrleansPingState(int Last)
{
    public OrleansPingState() : this(0) { }
}

public sealed record OrleansReactorState(int[] Received)
{
    public OrleansReactorState() : this([]) { }
}

[Neuron("probe.os09.orleans.source", State = "os09-source")]
public sealed class OrleansPingSourceNeuron : Neuron<OrleansPingState>, IOrleansPingSource
{
    public Task Emit(int value, string secretBody)
        => Save(new OrleansPingState(value), new OrleansPing(value, secretBody));
}

[Neuron("probe.os09.orleans.other", State = "os09-other")]
public sealed class OrleansOtherPingSourceNeuron : Neuron<OrleansPingState>, IOrleansOtherPingSource
{
    public Task Emit(int value, string secretBody)
        => Save(new OrleansPingState(value), new OrleansPing(value, secretBody));
}

[Neuron("probe.os09.orleans.reactor", State = "os09-reactor")]
[Reacts(typeof(IOrleansPingSource), typeof(OrleansPing))]
public sealed class OrleansPingReactorNeuron : Neuron<OrleansReactorState>, IOrleansPingReactor
{
    public Task<int[]> Received() => Task.FromResult(State?.Received ?? []);

    [On]
    public Task On(OrleansPing signal)
    {
        var next = (State?.Received ?? []).Append(signal.Value).ToArray();
        return Save(new OrleansReactorState(next), new OrleansPingAck(signal.Value, signal.SecretBody));
    }
}
