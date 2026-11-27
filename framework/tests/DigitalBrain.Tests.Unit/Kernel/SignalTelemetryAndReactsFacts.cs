using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Signals;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Kernel;

public sealed class SignalTelemetryAndReactsFacts
{
    private const string SecretPayload = "SECRET_PAYLOAD_DO_NOT_LEAK_9f8e7d";

    [Signal("probe.telemetry.ping")]
    public sealed record TelemetryPing(int Sequence, string SecretBody) : Signal;

    [Signal("probe.telemetry.pong")]
    public sealed record TelemetryPong(int Sequence, string SecretBody) : Signal;

    public interface IPingSource
    {
        Task Emit(int value, string secretBody);
    }

    public interface IOtherPingSource
    {
        Task Emit(int value, string secretBody);
    }

    public interface IPingReactor
    {
        Task<int[]> Received();
    }

    public sealed record CounterRecord(int Last)
    {
        public CounterRecord() : this(0) { }
    }

    public sealed record ReactorRecord(int[] Received)
    {
        public ReactorRecord() : this([]) { }
    }

    [Neuron("probe.os09.source")]
    public sealed class PingSourceNeuron : Neuron<CounterRecord>, IPingSource
    {
        public Task Emit(int value, string secretBody)
            => Save(new CounterRecord(value), new TelemetryPing(value, secretBody));
    }

    [Neuron("probe.os09.other-source")]
    public sealed class OtherPingSourceNeuron : Neuron<CounterRecord>, IOtherPingSource
    {
        public Task Emit(int value, string secretBody)
            => Save(new CounterRecord(value), new TelemetryPing(value, secretBody));
    }

    [Neuron("probe.os09.reactor")]
    [Reacts(typeof(IPingSource), typeof(TelemetryPing))]
    public sealed class DeclarativePingReactorNeuron : Neuron<ReactorRecord>, IPingReactor
    {
        public Task<int[]> Received() => Task.FromResult(State?.Received ?? []);

        [On]
        public Task On(TelemetryPing signal)
        {
            var next = (State?.Received ?? []).Append(signal.Sequence).ToArray();
            return Save(new ReactorRecord(next), new TelemetryPong(signal.Sequence, signal.SecretBody));
        }
    }

    [Neuron("probe.os09.self-emitter")]
    [Reacts(typeof(SelfEmittingReactorNeuron), typeof(TelemetryPing))]
    [Reacts(typeof(IPingSource), typeof(TelemetryPing))]
    public sealed class SelfEmittingReactorNeuron : Neuron<ReactorRecord>, IPingReactor
    {
        public Task<int[]> Received() => Task.FromResult(State?.Received ?? []);

        [On]
        public Task On(TelemetryPing signal)
        {
            var next = (State?.Received ?? []).Append(signal.Sequence).ToArray();
            // Emitting the same signal type we react to must never trigger a self-loop.
            return Save(new ReactorRecord(next), new TelemetryPing(signal.Sequence + 1000, signal.SecretBody));
        }
    }

    [Neuron("probe.os09.failing-reactor")]
    [Reacts(typeof(IPingSource), typeof(TelemetryPing))]
    public sealed class FailingReactorNeuron : Neuron
    {
        [On]
        public Task On(TelemetryPing signal)
        {
            _ = Id;
            throw new InvalidOperationException($"Boom: {signal.SecretBody}");
        }
    }

    [Neuron("probe.os09.manifest-reactor")]
    public sealed class ManifestOnlyReactorNeuron : Neuron<ReactorRecord>, IPingReactor
    {
        public Task<int[]> Received() => Task.FromResult(State?.Received ?? []);

        [On]
        public Task On(TelemetryPing signal)
        {
            var next = (State?.Received ?? []).Append(signal.Sequence).ToArray();
            return Store(new ReactorRecord(next));
        }
    }

    [Fact]
    public async Task SignalTelemetry_EmitsPublishAndReactSpansWithSafeMetadataAndNeverLeaksPayload()
    {
        var captured = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SignalTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<DeclarativePingReactorNeuron>();

        var brain = new BrainId("b-telemetry");
        var sourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var reactorId = new NeuronId(brain, "probe.os09.reactor", "default");

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            await os.Get<IPingSource>(sourceId).Emit(42, SecretPayload);
            await os.Idle();
        }

        var activities = captured
            .Where(a => string.Equals(a.GetTagItem(SignalTelemetry.PublisherTag) as string, sourceId.ToString(), StringComparison.Ordinal)
                     || string.Equals(a.GetTagItem(SignalTelemetry.PublisherTag) as string, reactorId.ToString(), StringComparison.Ordinal))
            .ToArray();

        var publishPing = Assert.Single(activities, a =>
            a.OperationName == SignalTelemetry.PublishSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.telemetry.ping", StringComparison.Ordinal));
        Assert.Equal(ActivityKind.Producer, publishPing.Kind);
        Assert.Equal(sourceId.ToString(), publishPing.GetTagItem(SignalTelemetry.PublisherTag));
        Assert.Equal(1L, publishPing.GetTagItem(SignalTelemetry.SequenceTag));
        Assert.Null(publishPing.GetTagItem(SignalTelemetry.SubscriberTag));

        var reactPing = Assert.Single(activities, a =>
            a.OperationName == SignalTelemetry.ReactSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.telemetry.ping", StringComparison.Ordinal));
        Assert.Equal(ActivityKind.Consumer, reactPing.Kind);
        Assert.Equal(sourceId.ToString(), reactPing.GetTagItem(SignalTelemetry.PublisherTag));
        Assert.Equal(reactorId.ToString(), reactPing.GetTagItem(SignalTelemetry.SubscriberTag));
        Assert.Equal(1L, reactPing.GetTagItem(SignalTelemetry.SequenceTag));
        Assert.Equal(Causation.Address(sourceId, 1), reactPing.GetTagItem(SignalTelemetry.CausationIdTag));

        var publishPong = Assert.Single(activities, a =>
            a.OperationName == SignalTelemetry.PublishSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.AliasTag) as string, "probe.telemetry.pong", StringComparison.Ordinal));
        Assert.Equal(reactorId.ToString(), publishPong.GetTagItem(SignalTelemetry.PublisherTag));
        Assert.Equal(1L, publishPong.GetTagItem(SignalTelemetry.SequenceTag));
        Assert.Equal(Causation.Address(sourceId, 1), publishPong.GetTagItem(SignalTelemetry.CausationIdTag));

        // Strict PII/payload safety check across every captured span.
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
    public async Task SignalTelemetry_ReactionFailureRecordsErrorTypeOnlyAndNeverLeaksExceptionMessage()
    {
        var captured = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SignalTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<FailingReactorNeuron>();

        var brain = new BrainId("b-telemetry-fail");
        var sourceId = new NeuronId(brain, "probe.os09.source", "s1");

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            await os.Get<IPingSource>(sourceId).Emit(7, SecretPayload);
            await os.Idle();
        }

        var failedReacts = captured.Where(a =>
            a.OperationName == SignalTelemetry.ReactSpanName
            && string.Equals(a.GetTagItem(SignalTelemetry.PublisherTag) as string, sourceId.ToString(), StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(failedReacts);
        Assert.All(failedReacts, failedReact =>
        {
            Assert.Equal(ActivityStatusCode.Error, failedReact.Status);
            Assert.Equal(nameof(InvalidOperationException), failedReact.StatusDescription);
            Assert.DoesNotContain(SecretPayload, failedReact.StatusDescription ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DeclarativeReacts_LazyActivatesNeverActivatedNeuron_FiltersSource_AndSurvivesRestart()
    {
        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<OtherPingSourceNeuron>();
        os.Register<DeclarativePingReactorNeuron>();

        var brain = new BrainId("b-reacts");
        var sourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var otherSourceId = new NeuronId(brain, "probe.os09.other-source", "o1");
        var reactorId = new NeuronId(brain, "probe.os09.reactor", "default");

        Assert.False(os.IsActive(reactorId));

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            // Non-matching source must NOT wake or deliver to [Reacts(typeof(IPingSource), ...)].
            await os.Get<IOtherPingSource>(otherSourceId).Emit(99, "ignored");
            await os.Idle();
            Assert.False(os.IsActive(reactorId));

            // Matching source lazy-activates the never-activated neuron and delivers the signal.
            await os.Get<IPingSource>(sourceId).Emit(1, "first");
            await os.Idle();
            Assert.True(os.IsActive(reactorId));
            var firstReceived = await os.Get<IPingReactor>(reactorId).Received();
            Assert.Equal([1], firstReceived);

            // Restart must not replay signal 1, and must deliver signal 2.
            await os.Restart();
            Assert.False(os.IsActive(reactorId));

            await os.Get<IPingSource>(sourceId).Emit(2, "second");
            await os.Idle();
            var secondReceived = await os.Get<IPingReactor>(reactorId).Received();
            Assert.Equal([1, 2], secondReceived);
        }
    }

    [Fact]
    public async Task DeclarativeReacts_NeverSelfLoopsWhenSubscriberEmitsSameSignalType()
    {
        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<SelfEmittingReactorNeuron>();

        var brain = new BrainId("b-noloop");
        var sourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var selfEmitterId = new NeuronId(brain, "probe.os09.self-emitter", "default");

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            await os.Get<IPingSource>(sourceId).Emit(5, "once");
            await os.Idle();

            // Must receive only the external signal 5, never its own emitted 1005.
            var received = await os.Get<IPingReactor>(selfEmitterId).Received();
            Assert.Equal([5], received);
        }
    }

    [Fact]
    public async Task DeclarativeReacts_ComponentManifestReactsAlsoWiresNeverActivatedNeuron()
    {
        ComponentManifests.Register(
            typeof(ManifestOnlyReactorNeuron),
            new ComponentManifest(
                new ComponentId("probe.os09.manifest-reactor"),
                [],
                Reacts: [typeof(TelemetryPing)]));

        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<ManifestOnlyReactorNeuron>();

        var brain = new BrainId("b-manifest-reacts");
        var sourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var reactorId = new NeuronId(brain, "probe.os09.manifest-reactor", "default");

        Assert.False(os.IsActive(reactorId));

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            await os.Get<IPingSource>(sourceId).Emit(11, "via-manifest");
            await os.Idle();

            Assert.True(os.IsActive(reactorId));
            var received = await os.Get<IPingReactor>(reactorId).Received();
            Assert.Equal([11], received);
        }
    }
}
