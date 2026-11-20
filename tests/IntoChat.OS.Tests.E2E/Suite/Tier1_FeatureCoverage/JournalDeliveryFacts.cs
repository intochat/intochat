using System.Reflection;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;
using DigitalBrain.Flows;

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

[Signal("test.delivery_fact")]
public sealed record TestDeliveryFact(int Number, string Message) : Signal;

public sealed class JournalDeliveryFacts : E2ETestBase
{
    [Fact]
    public async Task T1_Delivery_Nudge_WithStaleOrDuplicatePosition_IsDroppedImmediately()
    {
        await using var os = TestOS.Local(options => options.DuplicateDelivery = true);
        os.Register<DeliveryPublisherNeuron>();
        os.Register<DeliverySubscriberNeuron>();

        var brain = NewBrain("nudge-dup");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var pubId = new NeuronId(brain, "delivery.publisher", "pub");
        var subId = new NeuronId(brain, "delivery.subscriber", "sub");

        var publisher = os.Get<IDeliveryPublisher>(pubId);
        var subscriber = os.Get<IDeliverySubscriber>(subId);

        await subscriber.Subscribe(pubId);

        var initialDropped = os.DuplicatesDropped;

        // Emit 3 signals. Under DuplicateDelivery = true, TestOS delivers duplicate nudges for each signal.
        await publisher.PublishFact(1, "first");
        await publisher.PublishFact(2, "second");
        await publisher.PublishFact(3, "third");
        await os.Idle();

        // Assert that cursor advanced to 3 and duplicates were dropped
        Assert.Equal(3L, os.Cursor(subId, pubId));
        Assert.True(os.DuplicatesDropped >= initialDropped + 3,
            $"Expected at least 3 duplicate nudges dropped, but got {os.DuplicatesDropped - initialDropped}");
        Assert.Empty(os.Failures);
    }

    [Fact]
    public async Task T1_Delivery_CatchUp_PumpsSignalsFromJournalViewAndAdvancesCursor()
    {
        await using var os = TestOS.Local();
        os.Register<DeliveryPublisherNeuron>();
        os.Register<DeliverySubscriberNeuron>();

        var brain = NewBrain("catchup-advance");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var pubId = new NeuronId(brain, "delivery.publisher", "pub");
        var subId = new NeuronId(brain, "delivery.subscriber", "sub");

        var publisher = os.Get<IDeliveryPublisher>(pubId);
        var subscriber = os.Get<IDeliverySubscriber>(subId);

        await subscriber.Subscribe(pubId);

        // Deactivate subscriber so signals accumulate in the journal without immediate delivery
        await os.Deactivate(subId);
        Assert.False(os.IsActive(subId));

        // Publish signals while subscriber is inactive
        await publisher.PublishFact(1, "accumulated-1");
        await publisher.PublishFact(2, "accumulated-2");
        await publisher.PublishFact(3, "accumulated-3");
        await os.Idle();

        // Verify subscriber cursor has not advanced yet
        Assert.True(os.Cursor(subId, pubId) is null or 0L);

        // Reactivate subscriber: on activation it catches up on its synapses
        await os.Activate(subId);
        await os.Idle();

        // Cursor has now advanced to 3
        Assert.Equal(3L, os.Cursor(subId, pubId));
        Assert.Empty(os.Failures);
    }

    [Fact]
    public async Task T1_Delivery_FailedReaction_RetreatsCursorAndRecordsFailure()
    {
        await using var os = TestOS.Local();
        os.Register<DeliveryPublisherNeuron>();
        os.Register<FaultySubscriberNeuron>();

        var brain = NewBrain("failed-reaction");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var pubId = new NeuronId(brain, "delivery.publisher", "pub");
        var subId = new NeuronId(brain, "delivery.faulty", "sub");

        var publisher = os.Get<IDeliveryPublisher>(pubId);
        var subscriber = os.Get<IFaultySubscriber>(subId);

        await subscriber.Subscribe(pubId);

        // Publish first signal that succeeds
        await publisher.PublishFact(1, "success");
        await os.Idle();

        Assert.Equal(1L, os.Cursor(subId, pubId));
        Assert.Empty(os.Failures);

        // Arm subscriber to fail on next reaction
        await subscriber.ArmFailure(true);

        // Publish second signal - reaction throws
        await publisher.PublishFact(2, "fail");
        await os.Idle();

        // Verification 1: Failure recorded in TestOS.Failures
        Assert.NotEmpty(os.Failures);
        Assert.Contains(os.Failures, ex => ex.Message.Contains("Simulated reaction failure"));

        // Verification 2: Cursor retreated and did NOT advance to 2
        Assert.Equal(1L, os.Cursor(subId, pubId));

        // Disarm failure: subscriber recovers
        await subscriber.ArmFailure(false);

        // Reactivate subscriber to retry
        await os.Deactivate(subId);
        await os.Activate(subId);
        await os.Idle();

        // Cursor advances to 2 after successful retry
        Assert.Equal(2L, os.Cursor(subId, pubId));
    }

    [Fact]
    public async Task T1_Delivery_TruncatedJournal_EmitsSyntheticGapSignal()
    {
        await using var os = TestOS.Local(options =>
        {
            options.HistoryWindowSignals = 2;
            options.HistoryWindowAge = TimeSpan.FromMinutes(1);
            options.StaleCursorAfter = TimeSpan.FromMinutes(1);
        });
        os.Register<DeliveryPublisherNeuron>();
        os.Register<GapTrackingSubscriberNeuron>();

        var brain = NewBrain("gap-delivery");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var pubId = new NeuronId(brain, "delivery.publisher", "pub");
        var subId = new NeuronId(brain, "delivery.gap_tracker", "sub");

        var publisher = os.Get<IDeliveryPublisher>(pubId);
        var subscriber = os.Get<IGapTrackingSubscriber>(subId);

        await subscriber.Subscribe(pubId);

        // Signal 1 delivered normally
        await publisher.PublishFact(1, "before-truncation");
        await os.Idle();
        Assert.Equal(1L, os.Cursor(subId, pubId));

        // Deactivate subscriber so its cursor falls behind
        await os.Deactivate(subId);

        // Publish signals 2 through 6
        for (var i = 2; i <= 6; i++)
        {
            await publisher.PublishFact(i, $"fact-{i}");
        }
        await os.Idle();

        // Advance clock past stale cursor window and force checkpoint to truncate journal history
        os.Clock.Advance(TimeSpan.FromMinutes(2));
        os.ForceCheckpoint(brain);

        // Re-activate subscriber: journal history truncated past cursor (1) -> Delivery emits synthetic Gap signal
        await os.Activate(subId);
        await os.Idle();

        // Verify subscriber received synthetic Gap signal
        var receivedGap = await subscriber.GetLastGap();
        Assert.NotNull(receivedGap);
        Assert.Equal(pubId, receivedGap.Source);
        Assert.True(receivedGap.From <= receivedGap.To);

        // Final cursor advances to latest signal
        Assert.Equal(6L, os.Cursor(subId, pubId));
    }

    [Fact]
    public void T1_Delivery_OneWayNudge_IsNonBlockingAndDecoupledFromPersistentStreams()
    {
        var synapseHostType = typeof(RuntimeOptions).Assembly.GetType("DigitalBrain.Runtime.Orleans.Synapses.ISynapseHost");
        Assert.NotNull(synapseHostType);

        var nudgeMethod = synapseHostType.GetMethod("Nudge", [typeof(long)]);
        Assert.NotNull(nudgeMethod);
        var nudgeOneWay = nudgeMethod.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "OneWayAttribute");
        Assert.NotNull(nudgeOneWay);

        var catchUpMethod = synapseHostType.GetMethod("CatchUp", Type.EmptyTypes);
        Assert.NotNull(catchUpMethod);
        var catchUpOneWay = catchUpMethod.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "OneWayAttribute");
        Assert.NotNull(catchUpOneWay);

        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var refs = orleansAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.DoesNotContain("Microsoft.Orleans.Streaming", refs);
        Assert.DoesNotContain("Orleans.Streaming", refs);

        var runtimeOptionsType = typeof(RuntimeOptions);
        Assert.Null(runtimeOptionsType.GetProperty("PubSubStore"));
        Assert.Null(runtimeOptionsType.GetProperty("SignalStreams"));
    }

    [Fact]
    public async Task T1_Delivery_ConcurrentNudgesAndPumps_DoNotDeadlockOrDropSignals()
    {
        await using var os = TestOS.Local(options => options.DuplicateDelivery = true);
        os.Register<DeliveryPublisherNeuron>();
        os.Register<ConcurrentCountingSubscriberNeuron>();

        var brain = NewBrain("concurrent-delivery");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var pubId = new NeuronId(brain, "delivery.publisher", "pub");
        var subId = new NeuronId(brain, "delivery.concurrent", "sub");

        var publisher = os.Get<IDeliveryPublisher>(pubId);
        var subscriber = os.Get<IConcurrentCountingSubscriber>(subId);

        // Genuine subscription registered
        await subscriber.Subscribe(pubId);
        await os.Idle();

        const int signalCount = 50;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Concurrently publish signals across multiple tasks
        var publishTasks = Enumerable.Range(1, signalCount)
            .Select(i => Task.Run(async () =>
            {
                await publisher.PublishFact(i, $"concurrent-{i}");
            }, cts.Token))
            .ToList();

        await Task.WhenAll(publishTasks);
        await os.Idle();

        // 1. Assert deadlock-freedom
        Assert.False(cts.IsCancellationRequested, "Concurrent publication and delivery must complete without timing out or deadlocking.");

        // 2. Assert zero signal loss: all signals consumed
        var receivedCount = await subscriber.GetReceivedCount();
        Assert.Equal(signalCount, receivedCount);

        // 3. Assert cursor advanced to total signal count
        Assert.Equal(50L, os.Cursor(subId, pubId));

        // 4. Assert zero unhandled failures
        Assert.Empty(os.Failures);
    }

    [Neuron("delivery.publisher")]
    public sealed class DeliveryPublisherNeuron : Neuron, IDeliveryPublisher
    {
        public Task PublishFact(int number, string message) =>
            Publish(new TestDeliveryFact(number, message));
    }

    public interface IDeliveryPublisher
    {
        Task PublishFact(int number, string message);
    }

    [Neuron("delivery.subscriber")]
    public sealed class DeliverySubscriberNeuron : Neuron, IDeliverySubscriber
    {
        public Task Subscribe(NeuronId publisher) =>
            Context.Wires.Ensure<TestDeliveryFact>(publisher);

        [On]
        public Task On(TestDeliveryFact fact)
        {
            _ = fact;
            return Task.CompletedTask;
        }
    }

    public interface IDeliverySubscriber
    {
        Task Subscribe(NeuronId publisher);
    }

    [Neuron("delivery.faulty")]
    public sealed class FaultySubscriberNeuron : Neuron, IFaultySubscriber
    {
        private static volatile bool _shouldThrow;

        public Task Subscribe(NeuronId publisher) =>
            Context.Wires.Ensure<TestDeliveryFact>(publisher);

        public Task ArmFailure(bool shouldThrow)
        {
            _shouldThrow = shouldThrow;
            return Task.CompletedTask;
        }

        [On]
        public Task On(TestDeliveryFact fact)
        {
            if (_shouldThrow)
            {
                throw new InvalidOperationException($"Simulated reaction failure for fact {fact.Number}");
            }
            return Task.CompletedTask;
        }
    }

    public interface IFaultySubscriber
    {
        Task Subscribe(NeuronId publisher);
        Task ArmFailure(bool shouldThrow);
    }

    [Neuron("delivery.gap_tracker")]
    public sealed class GapTrackingSubscriberNeuron : Neuron, IGapTrackingSubscriber
    {
        private static volatile Gap? _lastGap;

        public Task Subscribe(NeuronId publisher) =>
            Context.Wires.Ensure<TestDeliveryFact>(publisher);

        [On]
        public Task On(TestDeliveryFact fact)
        {
            _ = fact;
            return Task.CompletedTask;
        }

        [On]
        public Task On(Gap gap)
        {
            _lastGap = gap;
            return Task.CompletedTask;
        }

        public Task<Gap?> GetLastGap() => Task.FromResult(_lastGap);
    }

    public interface IGapTrackingSubscriber
    {
        Task Subscribe(NeuronId publisher);
        Task<Gap?> GetLastGap();
    }

    [Neuron("delivery.concurrent")]
    public sealed class ConcurrentCountingSubscriberNeuron : Neuron, IConcurrentCountingSubscriber
    {
        private static int _receivedCount;

        public Task Subscribe(NeuronId publisher)
        {
            _receivedCount = 0;
            return Context.Wires.Ensure<TestDeliveryFact>(publisher);
        }

        [On]
        public Task On(TestDeliveryFact fact)
        {
            _ = fact;
            Interlocked.Increment(ref _receivedCount);
            return Task.CompletedTask;
        }

        public Task<int> GetReceivedCount() => Task.FromResult(Volatile.Read(ref _receivedCount));
    }

    public interface IConcurrentCountingSubscriber
    {
        Task Subscribe(NeuronId publisher);
        Task<int> GetReceivedCount();
    }
}
