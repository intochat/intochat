using DigitalBrain.Runtime.Orleans.Signalling;
using DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;
using DigitalBrain.Runtime.Orleans.Tests.Integration.Synapses;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Signalling;

/// <summary>A session silo with a tiny pending queue for testing full-session backpressure.</summary>
public sealed class SmallQueueSessionSilo : SessionSilo
{
    protected override IReadOnlyDictionary<string, string?> Configuration { get; } = new Dictionary<string, string?>
    {
        [RuntimeOptions.Default.SignallingSection + ":SessionSweepEvery"] = "00:00:00.200",
        [RuntimeOptions.Default.SignallingSection + ":MaxPendingSignals"] = "3",
        [RuntimeOptions.Default.SignallingSection + ":NextTimeout"] = "00:00:02",
    };
}

// The csharp runtime hears plain sources through a session neuron: a signal published after the subscription is in its next batch.
public sealed class SessionDeliveryFacts(SessionSilo silo) : IClassFixture<SessionSilo>
{
    [Fact]
    public async Task ASignalPublishedAfterSubscribingArrivesInTheNextBatch()
    {
        using var platform = TestStamp.Kernel();
        var ct = TestContext.Current.CancellationToken;
        var source = NeuronId.Parse("b1/probe.counter/session-delivery");
        var session = silo.Get<ISessionSynapses>("b1/runtime.session/session-delivery");
        await session.Subscribe(source, ["probe.counted"]);

        await silo.Get<ICounter>(source.ToString()).Add(3);

        var batch = await NextBatch(session, ct);

        Assert.NotNull(batch);
        Assert.Contains(batch.Signals, signal => signal.Source == source.ToString());
    }

    [Fact]
    public async Task AnEphemeralSignalArrivesWithoutAJournalPosition()
    {
        using var platform = TestStamp.Kernel();
        var ct = TestContext.Current.CancellationToken;
        var source = NeuronId.Parse("b1/probe.counter/session-ephemeral");
        var session = silo.Get<ISessionSynapses>("b1/runtime.session/session-ephemeral");
        await session.Subscribe(source, ["probe.ephemeral"]);

        await silo.Get<ICounter>(source.ToString()).PingEphemeral("hi");

        var batch = await NextBatch(session, ct, timeoutSeconds: 5);
        Assert.NotNull(batch);
        var signal = Assert.Single(batch.Signals);
        Assert.Equal(("probe.ephemeral", 0L), (signal.Alias, signal.Sequence));
        Assert.Equal(source.ToString(), signal.Source);
    }

    [Fact]
    public async Task AnEphemeralSignalIsNeverJournaled()
    {
        using var platform = TestStamp.Kernel();
        var ct = TestContext.Current.CancellationToken;
        var source = NeuronId.Parse("b1/probe.counter/session-ephemeral-journal");
        var journal = silo.Services.GetRequiredService<IBrainJournalWriter>();

        await silo.Get<ICounter>(source.ToString()).PingEphemeral("hi");

        Assert.Null(await SignalLog.Newest(journal, source, "probe.ephemeral", ct));
    }

    private static async Task<SessionBatch?> NextBatch(ISessionSynapses session, CancellationToken ct, int timeoutSeconds = 20)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        SessionBatch? batch = null;
        while (batch is null && !limit.IsCancellationRequested)
        {
            try { batch = await session.Next(0, limit.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        return batch;
    }
}

public sealed class SessionQueueLimitFacts(SmallQueueSessionSilo silo) : IClassFixture<SmallQueueSessionSilo>
{
    [Fact]
    public async Task AFullSessionDropsOffersAndKeepsItsQueue()
    {
        using var platform = TestStamp.Kernel();
        var ct = TestContext.Current.CancellationToken;
        var source = NeuronId.Parse("b1/probe.counter/session-full");
        var sessionId = NeuronId.Parse("b1/runtime.session/session-full");
        var session = silo.Get<ISessionSynapses>(sessionId.ToString());
        await session.Subscribe(source, ["probe.counted", "probe.ephemeral"]);

        var counter = silo.Get<ICounter>(source.ToString());
        for (var i = 0; i < 3; i++) { await counter.Add(1); }

        // Wait until all 3 journaled signals have been delivered into the session queue (filling MaxPendingSignals = 3).
        var logs = silo.Services.GetRequiredService<BrainLogs>();
        var log = await logs.For("b1");
        await Sessions.Until(() =>
        {
            var entry = log.Subscriptions.Of(sessionId)?.Of(source);
            return Task.FromResult(entry is not null && entry.Cursor.Value >= log.LastOf(source).Value && log.LastOf(source).Value > 0);
        }, ct);

        // Offer an ephemeral signal to the full session: must not throw and must not displace queued signals.
        await counter.PingEphemeral("overflow");
        await Sessions.Until(() => Task.FromResult(silo.Counters.EphemeralOffersDropped > 0), ct, seconds: 5);

        Assert.True(silo.Counters.EphemeralOffersDropped >= 1);
        Assert.Equal(0, silo.Counters.ReactionFailures);

        var batch = await session.Next(0, ct);
        Assert.NotNull(batch);
        Assert.Equal(3, batch.Signals.Count);
        Assert.All(batch.Signals, s => Assert.Equal("probe.counted", s.Alias));
    }
}
