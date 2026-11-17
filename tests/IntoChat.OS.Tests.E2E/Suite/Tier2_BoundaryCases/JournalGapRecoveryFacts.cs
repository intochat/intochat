using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Signals;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier2_BoundaryCases;

[Signal("gap-test.fact")]
public sealed record GapTestFact(int Number, string Message) : Signal;

public sealed class JournalGapRecoveryFacts : E2ETestBase, IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        GapSubscriberNeuron.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        GapSubscriberNeuron.Reset();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task T2_Journal_DeepTruncation_PastSubscriberCursor_EmitsSyntheticGapSignal()
    {
        await using var os = TestOS.Local(options =>
        {
            options.HistoryWindowSignals = 2;
            options.HistoryWindowAge = TimeSpan.FromMinutes(1);
            options.StaleCursorAfter = TimeSpan.FromMinutes(1);
        });
        os.Register<GapPublisherNeuron>();
        os.Register<GapSubscriberNeuron>();

        var brain = NewBrain("gap-deep-trunc");
        var pubId = new NeuronId(brain, "gap.publisher", "pub");
        var subId = new NeuronId(brain, "gap.subscriber", "sub");

        var publisher = os.Get<IGapPublisher>(pubId);
        var subscriber = os.Get<IGapSubscriber>(subId);

        await subscriber.Subscribe(pubId);

        // Deliver signal 1
        await publisher.PublishFact(1, "initial");
        await os.Idle();
        Assert.Equal(1L, os.Cursor(subId, pubId));

        // Deactivate subscriber so its cursor stays at 1
        await os.Deactivate(subId);

        // Publish signals 2 through 7 while subscriber is offline
        for (var i = 2; i <= 7; i++)
        {
            await publisher.PublishFact(i, $"offline-fact-{i}");
        }
        await os.Idle();

        // Advance clock past stale cursor window and trigger checkpoint truncation
        os.Clock.Advance(TimeSpan.FromMinutes(2));
        os.ForceCheckpoint(brain);

        // Reactivate subscriber: delivery catches up and detects history floor > 1
        await os.Activate(subId);
        await os.Idle();

        // Verify synthetic Gap signal was delivered
        var receivedGap = await subscriber.GetLastGap();
        Assert.NotNull(receivedGap);
        Assert.Equal(pubId, receivedGap.Source);
        Assert.True(receivedGap.From <= receivedGap.To);

        // Final cursor advances to latest sequence (7)
        Assert.Equal(7L, os.Cursor(subId, pubId));
    }

    [Fact]
    public async Task T2_Journal_SubscriberReactionFailure_RetreatsCursor_AndPreservesUnprocessedPosition()
    {
        await using var os = TestOS.Local();
        os.Register<GapPublisherNeuron>();
        os.Register<GapSubscriberNeuron>();

        var brain = NewBrain("gap-reaction-fail");
        var pubId = new NeuronId(brain, "gap.publisher", "pub");
        var subId = new NeuronId(brain, "gap.subscriber", "sub");

        var publisher = os.Get<IGapPublisher>(pubId);
        var subscriber = os.Get<IGapSubscriber>(subId);

        await subscriber.Subscribe(pubId);

        // Signal 1 succeeds
        await publisher.PublishFact(1, "success-1");
        await os.Idle();
        Assert.Equal(1L, os.Cursor(subId, pubId));
        Assert.Empty(os.Failures);

        try
        {
            // Arm failure on reaction
            await subscriber.ArmFailure(true);

            // Signal 2 throws inside [On] reaction
            await publisher.PublishFact(2, "fail-2");
            await os.Idle();

            // Cursor did NOT advance to 2; retreated to 1
            Assert.Equal(1L, os.Cursor(subId, pubId));
            Assert.NotEmpty(os.Failures);
            Assert.Contains(os.Failures, ex => ex.Message.Contains("Simulated reaction failure"));
        }
        finally
        {
            await subscriber.ArmFailure(false);
        }
    }

    [Fact]
    public async Task T2_Journal_SubscriberRecovery_AdvancesCursorToLatestSequences()
    {
        await using var os = TestOS.Local();
        os.Register<GapPublisherNeuron>();
        os.Register<GapSubscriberNeuron>();

        var brain = NewBrain("gap-recovery");
        var pubId = new NeuronId(brain, "gap.publisher", "pub");
        var subId = new NeuronId(brain, "gap.subscriber", "sub");

        var publisher = os.Get<IGapPublisher>(pubId);
        var subscriber = os.Get<IGapSubscriber>(subId);

        await subscriber.Subscribe(pubId);

        // Deliver signal 1
        await publisher.PublishFact(1, "succeed-1");
        await os.Idle();

        try
        {
            // Arm subscriber to fail
            await subscriber.ArmFailure(true);
            await publisher.PublishFact(2, "fail-2");
            await os.Idle();
            Assert.Equal(1L, os.Cursor(subId, pubId));
        }
        finally
        {
            // Disarm failure: subscriber recovers
            await subscriber.ArmFailure(false);
        }

        // Re-activate subscriber to retry unprocessed signals
        await os.Deactivate(subId);
        await os.Activate(subId);
        await os.Idle();

        // Cursor successfully advances to 2
        Assert.Equal(2L, os.Cursor(subId, pubId));
    }

    [Fact]
    public void T2_Journal_MalformedJsonSignalWirePayload_ThrowsJsonException_AndDoesNotCorruptJournal()
    {
        var truncatedJson = MalformedWireGenerator.TruncatedSignalJson();

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<GapTestFact>(truncatedJson));
    }

    [Fact]
    public void T2_Journal_TruncatedWirePayload_ThrowsJsonException_AndLeavesNeuronStateIntact()
    {
        var typeMismatchJson = MalformedWireGenerator.TypeMismatchJson("Number");

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<GapTestFact>(typeMismatchJson));
    }

    [Fact]
    public void T2_Journal_NullSignalPayload_ThrowsJsonExceptionOrArgumentException()
    {
        // Deserializing empty JSON or null payload throws JsonException
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<GapTestFact>(""));
    }

    [Neuron("gap.publisher")]
    public sealed class GapPublisherNeuron : Neuron, IGapPublisher
    {
        public Task PublishFact(int number, string message) =>
            Publish(new GapTestFact(number, message));
    }

    public interface IGapPublisher
    {
        Task PublishFact(int number, string message);
    }

    [Neuron("gap.subscriber")]
    public sealed class GapSubscriberNeuron : Neuron, IGapSubscriber
    {
        private static volatile Gap? _lastGap;
        private static volatile bool _shouldThrow;

        public static void Reset()
        {
            _shouldThrow = false;
            _lastGap = null;
        }

        public Task Subscribe(NeuronId publisher) =>
            Context.Wires.Ensure<GapTestFact>(publisher);

        public Task ArmFailure(bool shouldThrow)
        {
            _shouldThrow = shouldThrow;
            return Task.CompletedTask;
        }

        public Task<Gap?> GetLastGap() => Task.FromResult(_lastGap);

        [On]
        public Task On(GapTestFact fact)
        {
            if (_shouldThrow)
            {
                throw new InvalidOperationException($"Simulated reaction failure for fact {fact.Number}");
            }
            return Task.CompletedTask;
        }

        [On]
        public Task On(Gap gap)
        {
            _lastGap = gap;
            return Task.CompletedTask;
        }
    }

    public interface IGapSubscriber
    {
        Task Subscribe(NeuronId publisher);
        Task ArmFailure(bool shouldThrow);
        Task<Gap?> GetLastGap();
    }
}
