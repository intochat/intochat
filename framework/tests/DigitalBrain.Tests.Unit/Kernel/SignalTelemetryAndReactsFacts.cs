using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Kernel;
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

    [Signal("probe.telemetry.extra")]
    public sealed record TelemetryExtra(int Sequence) : Signal;

    public interface IPingSource
    {
        Task Emit(int value, string secretBody);
    }

    public interface IOtherPingSource
    {
        Task Emit(int value, string secretBody);
    }

    public interface IExtraSource
    {
        Task EmitPing(int value);
        Task EmitExtra(int value);
    }

    public interface IPingReactor
    {
        Task<int[]> Received();
    }

    public interface IHybridReactor
    {
        Task SubscribeExtraBrainWide();
        Task SubscribePingBrainWide();
        Task FollowExtraFrom(NeuronId source);
        Task DropBrainWide();
        Task<int[]> ReceivedPings();
        Task<int[]> ReceivedExtras();
    }

    public sealed record CounterRecord(int Last)
    {
        public CounterRecord() : this(0) { }
    }

    public sealed record ReactorRecord(int[] Received)
    {
        public ReactorRecord() : this([]) { }
    }

    public sealed record HybridReactorRecord(int[] Pings, int[] Extras)
    {
        public HybridReactorRecord() : this([], []) { }
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

    [Neuron("probe.os09.extra-source")]
    public sealed class ExtraSourceNeuron : Neuron<CounterRecord>, IExtraSource
    {
        public Task EmitPing(int value)
            => Save(new CounterRecord(value), new TelemetryPing(value, "extra"));

        public Task EmitExtra(int value)
            => Save(new CounterRecord(value), new TelemetryExtra(value));
    }

    [Neuron("probe.os09.hybrid-reactor")]
    [Reacts(typeof(IPingSource), typeof(TelemetryPing))]
    public sealed class HybridReactsAndImperativeNeuron : Neuron<HybridReactorRecord>, IHybridReactor
    {
        public Task SubscribeExtraBrainWide() => Context.Wires.EnsureBrainWide(typeof(TelemetryExtra));

        public Task SubscribePingBrainWide() => Context.Wires.EnsureBrainWide(typeof(TelemetryPing));

        public Task FollowExtraFrom(NeuronId source) => Context.Wires.Ensure<TelemetryExtra>(source);

        public Task DropBrainWide() => Context.Wires.RemoveBrainWide();

        public Task<int[]> ReceivedPings() => Task.FromResult(State?.Pings ?? []);

        public Task<int[]> ReceivedExtras() => Task.FromResult(State?.Extras ?? []);

        [On]
        public Task On(TelemetryPing signal)
        {
            var current = State ?? new HybridReactorRecord();
            return Store(current with { Pings = [.. current.Pings, signal.Sequence] });
        }

        [On]
        public Task On(TelemetryExtra signal)
        {
            var current = State ?? new HybridReactorRecord();
            return Store(current with { Extras = [.. current.Extras, signal.Sequence] });
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

    [Fact]
    public async Task DeclarativeReacts_WithImperativeEnsureBrainWideForOtherSignal_ReceivesOtherFromAnySourceWhileFilteringDeclarative()
    {
        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<ExtraSourceNeuron>();
        os.Register<HybridReactsAndImperativeNeuron>();

        var brain = new BrainId("b-reacts-imperative-other");
        var pingSourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var extraSourceId = new NeuronId(brain, "probe.os09.extra-source", "e1");
        var reactorId = new NeuronId(brain, "probe.os09.hybrid-reactor", "default");

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            var reactor = os.Get<IHybridReactor>(reactorId);
            await reactor.SubscribeExtraBrainWide();

            // Imperative EnsureBrainWide(TelemetryExtra) must deliver from ANY publisher,
            // while declarative [Reacts(IPingSource, TelemetryPing)] still filters out non-IPingSource.
            await os.Get<IExtraSource>(extraSourceId).EmitExtra(10);
            await os.Get<IExtraSource>(extraSourceId).EmitPing(99);
            await os.Get<IPingSource>(pingSourceId).Emit(1, "allowed");
            await os.Idle();

            var extrasBefore = await reactor.ReceivedExtras();
            var pingsBefore = await reactor.ReceivedPings();
            Assert.Equal([10], extrasBefore);
            Assert.Equal([1], pingsBefore);

            // Must preserve the same split across restart/deactivation.
            await os.Restart();

            await os.Get<IExtraSource>(extraSourceId).EmitExtra(20);
            await os.Get<IExtraSource>(extraSourceId).EmitPing(88);
            await os.Get<IPingSource>(pingSourceId).Emit(2, "allowed-after-restart");
            await os.Idle();

            var extrasAfter = await reactor.ReceivedExtras();
            var pingsAfter = await reactor.ReceivedPings();
            Assert.Equal([10, 20], extrasAfter);
            Assert.Equal([1, 2], pingsAfter);
        }
    }

    [Fact]
    public async Task DeclarativeReacts_WithImperativeEnsureBrainWideForSameSignal_ReceivesFromAnySource()
    {
        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<ExtraSourceNeuron>();
        os.Register<HybridReactsAndImperativeNeuron>();

        var brain = new BrainId("b-reacts-imperative-same");
        var pingSourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var extraSourceId = new NeuronId(brain, "probe.os09.extra-source", "e1");
        var reactorId = new NeuronId(brain, "probe.os09.hybrid-reactor", "default");

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(brain)))
        {
            var reactor = os.Get<IHybridReactor>(reactorId);
            await reactor.SubscribePingBrainWide();

            // Unfiltered imperative EnsureBrainWide(TelemetryPing) must allow signals from non-IPingSource too.
            await os.Get<IExtraSource>(extraSourceId).EmitPing(77);
            await os.Get<IPingSource>(pingSourceId).Emit(78, "matching");
            await os.Idle();

            var receivedPings = await reactor.ReceivedPings();
            Assert.Equal([77, 78], receivedPings);
        }
    }

    [Fact]
    public async Task DeclarativeReacts_DueAndReconcile_IncludesSubscriberWithPersistedPointToPointFollow()
    {
        // Part 1: Direct unit test of DeclarativeReactsIndex.Due when the subscriber already holds
        // a persisted point-to-point Follow subscription (no brain-wide entry covering the declarative alias).
        var brain = new BrainId("b-reacts-due-direct");
        var sourceId = new NeuronId(brain, "probe.os09.source", "s1");
        var extraSourceId = new NeuronId(brain, "probe.os09.extra-source", "e1");
        var reactorId = new NeuronId(brain, "probe.os09.hybrid-reactor", "default");

        var index = new DeclarativeReactsIndex();
        index.Register("probe.os09.hybrid-reactor", typeof(HybridReactsAndImperativeNeuron));

        var subscriptions = new SubscriptionIndex();
        var pointToPointOnly = Subscriptions.None(reactorId)
            .Add(extraSourceId, [SignalAliases.Of(typeof(TelemetryExtra))], new Position(1), DateTimeOffset.UtcNow);
        subscriptions.Fold(pointToPointOnly);

        var pingAlias = SignalAliases.Of(typeof(TelemetryPing));
        var stubView = new StubJournalView(
            subscriptions,
            [new AliasMark(sourceId, pingAlias, new Position(2))]);

        static Type? ClassOf(string type) => type switch
        {
            "probe.os09.source" => typeof(PingSourceNeuron),
            "probe.os09.extra-source" => typeof(ExtraSourceNeuron),
            "probe.os09.hybrid-reactor" => typeof(HybridReactsAndImperativeNeuron),
            _ => null,
        };

        static Type? SignalOf(string alias) => alias switch
        {
            "probe.telemetry.ping" => typeof(TelemetryPing),
            "probe.telemetry.extra" => typeof(TelemetryExtra),
            _ => null,
        };

        var dueWithPointToPointOnly = index.Due(brain, stubView, ClassOf, SignalOf);
        Assert.Equal([reactorId], dueWithPointToPointOnly);

        // Once a brain-wide subscription covering the declarative alias IS persisted,
        // DeclarativeReactsIndex.Due no longer returns the subscriber (Subscriptions.Due owns cursor lag).
        var withBrainWide = pointToPointOnly.AddBrainWide([pingAlias], new Position(2), DateTimeOffset.UtcNow);
        subscriptions.Fold(withBrainWide);
        var dueWithBrainWide = index.Due(brain, stubView, ClassOf, SignalOf);
        Assert.Empty(dueWithBrainWide);

        // Part 2: End-to-end TestOS reconciliation via Restart() when fanout was dropped after append.
        await using var os = TestOS.Local();
        os.Register<PingSourceNeuron>();
        os.Register<ExtraSourceNeuron>();
        os.Register<HybridReactsAndImperativeNeuron>();

        var osBrain = new BrainId("b-reacts-reconcile-e2e");
        var osPingSourceId = new NeuronId(osBrain, "probe.os09.source", "s1");
        var osExtraSourceId = new NeuronId(osBrain, "probe.os09.extra-source", "e1");
        var osReactorId = new NeuronId(osBrain, "probe.os09.hybrid-reactor", "default");

        using (((ICallerCarrier)os.Callers).Enter(Caller.OfKernel(osBrain)))
        {
            var reactor = os.Get<IHybridReactor>(osReactorId);
            await reactor.FollowExtraFrom(osExtraSourceId);
            await reactor.DropBrainWide();
            await os.Idle();

            // Simulate a crash between durable journal append and fanout nudge.
            os.FailNextPublishAfterAppend(osPingSourceId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                os.Get<IPingSource>(osPingSourceId).Emit(55, "reconcile-me"));
            await os.Idle();
            Assert.Empty(await reactor.ReceivedPings());

            // Restart runs TestDelivery.Behind() -> DeclarativeReactsIndex.Due and must wake & deliver.
            await os.Restart();
            var reconciledPings = await os.Get<IHybridReactor>(osReactorId).ReceivedPings();
            Assert.Equal([55], reconciledPings);
        }
    }

    private sealed class StubJournalView(SubscriptionIndex subscriptions, IReadOnlyList<AliasMark> marks) : IJournalView
    {
        public Position Last => marks.Count == 0 ? new Position(0) : marks.Max(m => m.Last);
        public SubscriptionIndex Subscriptions { get; } = subscriptions;
        public Position FloorOf(NeuronId? publisher) => new(0);
        public Position LastOf(NeuronId publisher) => marks.Where(m => m.Publisher == publisher).Select(m => m.Last).DefaultIfEmpty(new Position(0)).Max();
        public IEnumerable<AliasMark> Marks(NeuronId? publisher) => publisher is null ? marks : marks.Where(m => m.Publisher == publisher.Value);
        public long SequenceAt(NeuronId publisher, Position position) => position.Value;
        public Task<JournalPage> ReadAfter(Position after, NeuronId? publisher, int limit, CancellationToken ct)
            => Task.FromResult(new JournalPage(new Position(0), Last, []));
    }
}
