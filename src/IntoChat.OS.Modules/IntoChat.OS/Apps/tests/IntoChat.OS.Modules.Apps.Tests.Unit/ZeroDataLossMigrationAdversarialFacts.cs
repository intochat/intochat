using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ZeroDataLossMigrationAdversarialFacts
{
    private sealed record AdversarialSignal(int SequenceNumber, string Data) : Signal;

    private sealed record AdversarialNeuronState : NeuronState
    {
        public List<long> ProcessedPositions { get; init; } = [];
    }

    private sealed class ThreadSafeBrainSignalLog(long initialPosition = 0) : IBrainSignalLog
    {
        private readonly object _lock = new();
        private readonly List<(long Position, NeuronId Publisher, Signal Signal)> _log = [];
        private long _position = initialPosition;
        private int _getLatestPositionCallCount;

        public int GetLatestPositionCallCount => Volatile.Read(ref _getLatestPositionCallCount);

        public string BrainId => "adversarial-brain";

        public void SeedHistoricalSignals(int count, NeuronId publisher)
        {
            lock (_lock)
            {
                for (var i = 1; i <= count; i++)
                {
                    _position++;
                    _log.Add((_position, publisher, new AdversarialSignal((int)_position, $"historical-{_position}")));
                }
            }
        }

        public Task<long> GetLatestPositionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getLatestPositionCallCount);
            lock (_lock)
            {
                return Task.FromResult(_position);
            }
        }

        public Task<long> AppendSignalAsync(NeuronId publisher, Signal signal, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            return AppendSignalsAsync(publisher, [signal], ct);
        }

        public Task<long> AppendSignalsAsync(NeuronId publisher, IReadOnlyList<Signal> signals, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(signals);
            lock (_lock)
            {
                foreach (var sig in signals)
                {
                    _position++;
                    _log.Add((_position, publisher, sig));
                }
                return Task.FromResult(_position);
            }
        }

        public Task NotifySubscribersAsync(IEnumerable<string> subscribers, long position)
            => Task.CompletedTask;

        public async Task<TState> EnsureCaughtUpAsync<TState>(
            NeuronId subscriberId,
            TState currentState,
            Func<TState, Signal, CancellationToken, Task<TState>> handler,
            Func<TState, CancellationToken, Task> commit,
            long targetPosition,
            CancellationToken ct = default) where TState : NeuronState
        {
            List<(long Position, NeuronId Publisher, Signal Signal)> unhandled;
            lock (_lock)
            {
                unhandled = _log
                    .Where(e => e.Position > currentState.SignalCursor && e.Position <= targetPosition)
                    .OrderBy(e => e.Position)
                    .ToList();
            }

            var state = currentState;
            foreach (var entry in unhandled)
            {
                state = await handler(state, entry.Signal, ct).ConfigureAwait(false);
                state = state with { SignalCursor = entry.Position };
                await commit(state, ct).ConfigureAwait(false);
            }

            return state;
        }

        public bool IsQuarantined(DateTimeOffset lastActiveAt, DateTimeOffset? now = null) => false;

        public long ComputeCompactionFloor(
            long logLatestPosition,
            IEnumerable<(string SubscriberId, long Cursor, DateTimeOffset LastActiveAt)> subscribers,
            DateTimeOffset? now = null) => logLatestPosition;
    }

    private sealed class ThreadSafeLegacyRegistry : ISynapseCursorRegistry
    {
        private int _getCursorCallCount;
        public int GetCursorCallCount => Volatile.Read(ref _getCursorCallCount);
        public ConcurrentDictionary<(string Subscriber, string Publisher), long> Cursors { get; } = [];

        public Task<long?> GetCursor(string subscriber, NeuronId publisher)
        {
            Interlocked.Increment(ref _getCursorCallCount);
            if (Cursors.TryGetValue((subscriber, publisher.ToString()), out var cursor))
            {
                return Task.FromResult<long?>(cursor);
            }
            return Task.FromResult<long?>(null);
        }
    }

    private sealed class ThreadSafeGrainStorage : IGrainStorage
    {
        private int _readStateCallCount;
        public int ReadStateCallCount => Volatile.Read(ref _readStateCallCount);
        public ConcurrentDictionary<string, SynapseState> Storage { get; } = new(StringComparer.Ordinal);

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            Interlocked.Increment(ref _readStateCallCount);
            var key = grainId.Key.ToString() ?? "";
            if (Storage.TryGetValue(key, out var state) && state is T typed)
            {
                grainState.State = typed;
                grainState.RecordExists = true;
            }
            else
            {
                grainState.RecordExists = false;
            }
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
    }

    private sealed class CommitCounter
    {
        public int Count;
    }

    [Fact]
    public async Task ReplayStorm_TenThousandHistoricalSignals_OnlyPostCutoverSignalProcessed_PathA()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "storm-brain-a";
        var subscriberId = "app-sub-10k-a";
        var publisherId = NeuronId.Parse("storm-brain-a/feed/p1");

        // 1. Brain has 10,000 historical signals in its log
        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(10_000, publisherId);
        var latestBeforeMigration = await signalLog.GetLatestPositionAsync(ct);
        Assert.Equal(10_000L, latestBeforeMigration);

        // 2. Subscriber has legacy publisher-relative cursor of 5 in synapse storage
        var storage = new ThreadSafeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 5 }]
        };

        var appState = new AppState { SignalCursor = 0 };
        var persistedCount = 0;
        long committedCursor = -1;

        // 3. Resolve cursor via migration
        var resolvedCursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            appState,
            s => { Interlocked.Increment(ref persistedCount); committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            ct: ct);

        // Strict Watermark Invariant: SignalCursor must equal GetLatestPosition (10,000), NEVER legacy sequence 5
        Assert.Equal(10_000L, resolvedCursor);
        Assert.Equal(10_000L, appState.SignalCursor);
        Assert.Equal(10_000L, committedCursor);
        Assert.Equal(1, persistedCount);

        // 4. Append newly published signal 10,001 post-cutover
        var livePosition = await signalLog.AppendSignalAsync(publisherId, new AdversarialSignal(10_001, "live-signal-10001"), ct);
        Assert.Equal(10_001L, livePosition);

        // 5. Catch-up execution
        var neuronState = new AdversarialNeuronState { SignalCursor = appState.SignalCursor };
        var deliveredSignals = new List<AdversarialSignal>();

        var finalState = await signalLog.EnsureCaughtUpAsync(
            NeuronId.Parse($"{brain}/app/{subscriberId}"),
            neuronState,
            (s, sig, _) =>
            {
                var advSig = Assert.IsType<AdversarialSignal>(sig);
                deliveredSignals.Add(advSig);
                return Task.FromResult(s with
                {
                    ProcessedPositions = [.. s.ProcessedPositions, advSig.SequenceNumber]
                });
            },
            (s, _) => Task.CompletedTask,
            targetPosition: livePosition,
            ct: ct);

        // Historical signals 1..10,000 were strictly NEVER delivered!
        var singleSignal = Assert.Single(deliveredSignals);
        Assert.Equal(10_001, singleSignal.SequenceNumber);
        Assert.Equal("live-signal-10001", singleSignal.Data);
        Assert.Equal(10_001L, finalState.SignalCursor);
        Assert.Single(finalState.ProcessedPositions);
    }

    [Fact]
    public async Task ReplayStorm_TenThousandHistoricalSignals_OnlyPostCutoverSignalProcessed_PathB()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "storm-brain-b";
        var subscriberId = "app-sub-10k-b";
        var publisherId = NeuronId.Parse("storm-brain-b/feed/p2");

        // 1. Brain has 10,000 historical signals in its log
        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(10_000, publisherId);
        Assert.Equal(10_000L, await signalLog.GetLatestPositionAsync(ct));

        // 2. Subscriber has legacy publisher-relative cursor of 5 in legacy registry (not in storage)
        var storage = new ThreadSafeGrainStorage(); // empty
        var registry = new ThreadSafeLegacyRegistry();
        registry.Cursors[(subscriberId, publisherId.ToString())] = 5;

        var appState = new AppState { SignalCursor = 0 };
        var persistedCount = 0;
        long committedCursor = -1;

        // 3. Resolve cursor via migration
        var resolvedCursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            appState,
            s => { Interlocked.Increment(ref persistedCount); committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        // Watermark invariant verification
        Assert.Equal(10_000L, resolvedCursor);
        Assert.Equal(10_000L, appState.SignalCursor);
        Assert.Equal(10_000L, committedCursor);
        Assert.Equal(1, persistedCount);

        // 4. Append live signal 10,001
        var livePosition = await signalLog.AppendSignalAsync(publisherId, new AdversarialSignal(10_001, "live-signal-10001-reg"), ct);

        // 5. Catch up
        var neuronState = new AdversarialNeuronState { SignalCursor = appState.SignalCursor };
        var deliveredSignals = new List<AdversarialSignal>();

        var finalState = await signalLog.EnsureCaughtUpAsync(
            NeuronId.Parse($"{brain}/app/{subscriberId}"),
            neuronState,
            (s, sig, _) =>
            {
                var advSig = Assert.IsType<AdversarialSignal>(sig);
                deliveredSignals.Add(advSig);
                return Task.FromResult(s with
                {
                    ProcessedPositions = [.. s.ProcessedPositions, advSig.SequenceNumber]
                });
            },
            (s, _) => Task.CompletedTask,
            targetPosition: livePosition,
            ct: ct);

        // Verified: 0 historical signals delivered; exactly 1 live signal delivered
        var singleSignal = Assert.Single(deliveredSignals);
        Assert.Equal(10_001, singleSignal.SequenceNumber);
        Assert.Equal(10_001L, finalState.SignalCursor);
    }

    [Fact]
    public async Task Counterfactual_RawLegacyCursor_WouldCauseDisastrousReplayStorm()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "counterfactual-brain";
        var subscriberId = "app-sub-naive";
        var publisherId = NeuronId.Parse("counterfactual-brain/chat/p1");

        // Brain has 10,000 historical signals
        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(10_000, publisherId);

        // If a naive migration improperly copied raw sequence 5 into SignalCursor:
        var naiveCursor = 5L;
        var targetPosition = await signalLog.AppendSignalAsync(publisherId, new AdversarialSignal(10_001, "new"), ct);

        var deliveredCount = 0;
        var naiveState = new AdversarialNeuronState { SignalCursor = naiveCursor };

        await signalLog.EnsureCaughtUpAsync(
            NeuronId.Parse($"{brain}/app/{subscriberId}"),
            naiveState,
            (s, _, _) =>
            {
                deliveredCount++;
                return Task.FromResult(s);
            },
            (s, _) => Task.CompletedTask,
            targetPosition: targetPosition,
            ct: ct);

        // Emphasizes the blast radius: 9,996 historical signals would have been improperly re-executed!
        Assert.Equal(9_996, deliveredCount);
    }

    [Fact]
    public async Task ConcurrentMigration_HundredSubscribers_CutoverSimultaneously_AtomicAndIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "concurrent-brain";
        var publisherId = NeuronId.Parse("concurrent-brain/broadcast/main");

        // 1. Brain signal log with 5,000 historical signals
        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(5000, publisherId);
        Assert.Equal(5000L, await signalLog.GetLatestPositionAsync(ct));

        // 2. Setup 100 subscribers with legacy storage entries
        const int subscriberCount = 100;
        var storage = new ThreadSafeGrainStorage();
        var subscribers = new List<(string SubId, AppState State, CommitCounter Counter)>();

        for (var i = 1; i <= subscriberCount; i++)
        {
            var subId = $"subscriber-{i:D3}";
            storage.Storage[subId] = new SynapseState
            {
                Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = (i % 20) + 1 }]
            };
            subscribers.Add((subId, new AppState { SignalCursor = 0 }, new CommitCounter()));
        }

        // 3. Execute migration concurrently across all 100 subscribers
        var migrationTasks = subscribers.Select(async sub =>
        {
            var cursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
                brain,
                sub.SubId,
                publisherId,
                sub.State,
                _ =>
                {
                    Interlocked.Increment(ref sub.Counter.Count);
                    return Task.CompletedTask;
                },
                signalLog,
                clusterStorage: storage,
                ct: ct);
            return (sub.SubId, ResolvedCursor: cursor, StateCursor: sub.State.SignalCursor, CommitCount: sub.Counter.Count);
        });

        var results = await Task.WhenAll(migrationTasks);

        // 4. Assert invariant across all 100 subscribers
        Assert.Equal(subscriberCount, results.Length);
        foreach (var r in results)
        {
            Assert.Equal(5000L, r.ResolvedCursor);
            Assert.Equal(5000L, r.StateCursor);
            Assert.Equal(1, r.CommitCount);
        }

        // 5. Subsequent migrations on all 100 subscribers must be immediate no-ops (Primary Path)
        var secondRunTasks = subscribers.Select(async sub =>
        {
            var initialCommitCount = sub.Counter.Count;
            var cursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
                brain,
                sub.SubId,
                publisherId,
                sub.State,
                _ =>
                {
                    Interlocked.Increment(ref sub.Counter.Count);
                    return Task.CompletedTask;
                },
                signalLog,
                clusterStorage: storage,
                ct: ct);
            return (ResolvedCursor: cursor, StateCursor: sub.State.SignalCursor, ExtraCommits: sub.Counter.Count - initialCommitCount);
        });

        var secondResults = await Task.WhenAll(secondRunTasks);
        foreach (var r in secondResults)
        {
            Assert.Equal(5000L, r.ResolvedCursor);
            Assert.Equal(5000L, r.StateCursor);
            Assert.Equal(0, r.ExtraCommits); // No extra persist calls
        }
    }

    [Fact]
    public async Task ConcurrentMigration_SameSubscriber_MultipleRacingTurns_IdempotentAndThreadSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "race-brain";
        var subscriberId = "app-sub-racing";
        var publisherId = NeuronId.Parse("race-brain/feed/p1");

        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(2500, publisherId);

        var storage = new ThreadSafeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 12 }]
        };

        var appState = new AppState { SignalCursor = 0 };
        var stateLock = new object();
        var persistCalls = 0;

        // 50 concurrent racing calls to ResolveSubscriberCursorAsync on the same subscriber
        const int raceConcurrency = 50;
        var tasks = Enumerable.Range(0, raceConcurrency).Select(_ =>
        {
            return Task.Run(async () =>
            {
                return await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
                    brain,
                    subscriberId,
                    publisherId,
                    appState,
                    _ =>
                    {
                        lock (stateLock)
                        {
                            persistCalls++;
                        }
                        return Task.CompletedTask;
                    },
                    signalLog,
                    clusterStorage: storage,
                    ct: ct);
            });
        });

        var resolvedCursors = await Task.WhenAll(tasks);

        // Every single task must receive the exact cutover position (2500)
        Assert.All(resolvedCursors, c => Assert.Equal(2500L, c));
        Assert.Equal(2500L, appState.SignalCursor);

        // Check idempotency: subsequent call on the same state
        var subsequentCallPersisted = false;
        var nextCursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            appState,
            _ => { subsequentCallPersisted = true; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            ct: ct);

        Assert.Equal(2500L, nextCursor);
        Assert.False(subsequentCallPersisted);
    }

    [Fact]
    public async Task MultiPublisherSubscriber_FirstPublisherSeedsCutover_SecondPublisherHitsPrimaryPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "multi-pub-brain";
        var subscriberId = "app-multi-sub";
        var publisher1 = NeuronId.Parse("multi-pub-brain/pub/1");
        var publisher2 = NeuronId.Parse("multi-pub-brain/pub/2");

        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(1000, publisher1);

        var storage = new ThreadSafeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources =
            [
                new SynapseSource { Source = publisher1.ToString(), Cursor = 10 },
                new SynapseSource { Source = publisher2.ToString(), Cursor = 25 }
            ]
        };

        var state = new AppState { SignalCursor = 0 };
        var persistCount = 0;

        // 1. Resolve for Publisher 1 -> Seeds to 1000
        var cursor1 = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisher1,
            state,
            _ => { Interlocked.Increment(ref persistCount); return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            ct: ct);

        Assert.Equal(1000L, cursor1);
        Assert.Equal(1000L, state.SignalCursor);
        Assert.Equal(1, persistCount);
        Assert.Equal(1, storage.ReadStateCallCount);

        // 2. Resolve for Publisher 2 -> Hits Tier 1 (Primary Path) immediately!
        var cursor2 = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisher2,
            state,
            _ => { Interlocked.Increment(ref persistCount); return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            ct: ct);

        Assert.Equal(1000L, cursor2);
        Assert.Equal(1000L, state.SignalCursor);
        Assert.Equal(1, persistCount); // No second persist!
        Assert.Equal(1, storage.ReadStateCallCount); // No second read!
    }

    [Fact]
    public async Task MidCutoverSignalArrival_CatchUpPicksUpInterleavedSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "interleave-brain";
        var subscriberId = "app-interleave";
        var publisherId = NeuronId.Parse("interleave-brain/stream/main");

        var signalLog = new ThreadSafeBrainSignalLog();
        signalLog.SeedHistoricalSignals(1000, publisherId);

        var storage = new ThreadSafeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 5 }]
        };

        var state = new AppState { SignalCursor = 0 };

        // Migration runs at position 1000
        var cutoverCursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            _ => Task.CompletedTask,
            signalLog,
            clusterStorage: storage,
            ct: ct);

        Assert.Equal(1000L, cutoverCursor);

        // Signals 1001 and 1002 arrive immediately after cutover
        await signalLog.AppendSignalAsync(publisherId, new AdversarialSignal(1001, "msg-1001"), ct);
        var latestPos = await signalLog.AppendSignalAsync(publisherId, new AdversarialSignal(1002, "msg-1002"), ct);
        Assert.Equal(1002L, latestPos);

        // Subscriber catches up
        var neuronState = new AdversarialNeuronState { SignalCursor = state.SignalCursor };
        var handledList = new List<long>();

        var caughtUpState = await signalLog.EnsureCaughtUpAsync(
            NeuronId.Parse($"{brain}/app/{subscriberId}"),
            neuronState,
            (s, sig, _) =>
            {
                var adv = (AdversarialSignal)sig;
                handledList.Add(adv.SequenceNumber);
                return Task.FromResult(s);
            },
            (s, _) => Task.CompletedTask,
            targetPosition: latestPos,
            ct: ct);

        Assert.Equal([1001L, 1002L], handledList);
        Assert.Equal(1002L, caughtUpState.SignalCursor);
    }
}
