using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ZeroDataLossMigrationFacts
{
    private sealed record TestSignal(string Message) : Signal;

    private sealed record TestSubscriberNeuronState : NeuronState
    {
        public List<long> ProcessedPositions { get; init; } = [];
    }

    private sealed class FakeBrainSignalLog(long initialPosition = 0) : IBrainSignalLog
    {
        private readonly List<(long Position, NeuronId Publisher, Signal Signal)> _log = [];
        private long _position = initialPosition;

        public int GetLatestPositionCallCount { get; private set; }

        public string BrainId => "test-brain";

        public void SeedHistoricalSignals(int count, NeuronId publisher)
        {
            for (var i = 1; i <= count; i++)
            {
                _position++;
                _log.Add((_position, publisher, new TestSignal($"historical-{_position}")));
            }
        }

        public Task<long> GetLatestPositionAsync(CancellationToken ct = default)
        {
            GetLatestPositionCallCount++;
            return Task.FromResult(_position);
        }

        public Task<long> AppendSignalAsync(NeuronId publisher, Signal signal, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            return AppendSignalsAsync(publisher, [signal], ct);
        }

        public Task<long> AppendSignalsAsync(NeuronId publisher, IReadOnlyList<Signal> signals, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(signals);
            foreach (var sig in signals)
            {
                _position++;
                _log.Add((_position, publisher, sig));
            }
            return Task.FromResult(_position);
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
            var state = currentState;
            var unhandled = _log.Where(e => e.Position > state.SignalCursor && e.Position <= targetPosition).ToList();

            foreach (var entry in unhandled)
            {
                state = await handler(state, entry.Signal, ct);
                state = state with { SignalCursor = entry.Position };
                await commit(state, ct);
            }

            return state;
        }

        public bool IsQuarantined(DateTimeOffset lastActiveAt, DateTimeOffset? now = null)
            => false;

        public long ComputeCompactionFloor(
            long logLatestPosition,
            IEnumerable<(string SubscriberId, long Cursor, DateTimeOffset LastActiveAt)> subscribers,
            DateTimeOffset? now = null)
            => logLatestPosition;
    }

    private sealed class FakeLegacyRegistry : ISynapseCursorRegistry
    {
        public int GetCursorCallCount { get; private set; }
        public Dictionary<(string Subscriber, string Publisher), long> Cursors { get; } = [];
        public bool ThrowOnQuery { get; set; }

        public Task<long?> GetCursor(string subscriber, NeuronId publisher)
        {
            GetCursorCallCount++;
            if (ThrowOnQuery)
            {
                throw new TimeoutException("Legacy registry grain unreachable.");
            }

            if (Cursors.TryGetValue((subscriber, publisher.ToString()), out var cursor))
            {
                return Task.FromResult<long?>(cursor);
            }
            return Task.FromResult<long?>(null);
        }
    }

    private sealed class FakeGrainStorage : IGrainStorage
    {
        public int ReadStateCallCount { get; private set; }
        public Dictionary<string, SynapseState> Storage { get; } = new(StringComparer.Ordinal);
        public bool ThrowOnRead { get; set; }

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            ReadStateCallCount++;
            if (ThrowOnRead)
            {
                throw new IOException("Simulated transient cluster storage read failure.");
            }

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

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Task.CompletedTask;

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => Task.CompletedTask;
    }

    private sealed class TestLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task PrimaryPath_ReturnsExistingSignalCursor_WithoutTouchingLegacyStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-primary";
        var subscriberId = "app-sub-1";
        var publisherId = NeuronId.Parse("brain-primary/chat/p1");

        var state = new AppState { SignalCursor = 42 };
        var persisted = false;
        var signalLog = new FakeBrainSignalLog(initialPosition: 1000);
        var storage = new FakeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 999 }]
        };
        var registry = new FakeLegacyRegistry();
        registry.Cursors[(subscriberId, publisherId.ToString())] = 888;

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            _ => { persisted = true; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(42L, result);
        Assert.Equal(42L, state.SignalCursor);
        Assert.False(persisted, "Primary path must not invoke persistState.");
        Assert.Equal(0, signalLog.GetLatestPositionCallCount);
        Assert.Equal(0, storage.ReadStateCallCount);
        Assert.Equal(0, registry.GetCursorCallCount);
    }

    [Fact]
    public async Task LegacySynapseStorage_SeedsCutoverWatermark_AndCommitsAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-storage";
        var subscriberId = "app-sub-2";
        var publisherId = NeuronId.Parse("brain-storage/chat/p2");

        var state = new AppState { SignalCursor = 0 };
        var persistedCount = 0;
        long committedCursor = -1;

        var signalLog = new FakeBrainSignalLog(initialPosition: 500);
        var storage = new FakeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 15 }]
        };
        var registry = new FakeLegacyRegistry();

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            s => { persistedCount++; committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(500L, result);
        Assert.Equal(500L, state.SignalCursor);
        Assert.Equal(1, persistedCount);
        Assert.Equal(500L, committedCursor);
        Assert.Equal(1, signalLog.GetLatestPositionCallCount);
        Assert.Equal(1, storage.ReadStateCallCount);
        Assert.Equal(0, registry.GetCursorCallCount);
    }

    [Fact]
    public async Task LegacyRegistry_SeedsCutoverWatermark_WhenLocalStorageAbsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-reg";
        var subscriberId = "app-sub-3";
        var publisherId = NeuronId.Parse("brain-reg/chat/p3");

        var state = new AppState { SignalCursor = 0 };
        var persistedCount = 0;
        long committedCursor = -1;

        var signalLog = new FakeBrainSignalLog(initialPosition: 750);
        var storage = new FakeGrainStorage(); // No entry for subscriberId
        var registry = new FakeLegacyRegistry();
        registry.Cursors[(subscriberId, publisherId.ToString())] = 25;

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            s => { persistedCount++; committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(750L, result);
        Assert.Equal(750L, state.SignalCursor);
        Assert.Equal(1, persistedCount);
        Assert.Equal(750L, committedCursor);
        Assert.Equal(1, signalLog.GetLatestPositionCallCount);
        Assert.Equal(1, storage.ReadStateCallCount);
        Assert.Equal(1, registry.GetCursorCallCount);
    }

    [Fact]
    public async Task CutoverWatermark_EliminatesHistoricalReplayStorm()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-storm";
        var subscriberId = "app-sub-4";
        var publisherId = NeuronId.Parse("brain-storm/chat/p4");

        // 1. Brain signal log has 1,000 historical signals published before migration
        var signalLog = new FakeBrainSignalLog();
        signalLog.SeedHistoricalSignals(1000, publisherId);
        Assert.Equal(1000L, await signalLog.GetLatestPositionAsync(ct));

        // 2. Legacy storage had an old publisher-relative cursor of 5
        var storage = new FakeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 5 }]
        };

        var appState = new AppState { SignalCursor = 0 };
        var migratedCursor = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            appState,
            _ => Task.CompletedTask,
            signalLog,
            clusterStorage: storage,
            ct: ct);

        // Watermark invariant: cursor was seeded to the cutover horizon (1000)
        Assert.Equal(1000L, migratedCursor);
        Assert.Equal(1000L, appState.SignalCursor);

        // 3. Newly published signal after cutover lands at position 1001
        var newPosition = await signalLog.AppendSignalAsync(publisherId, new TestSignal("live-signal-post-cutover"), ct);
        Assert.Equal(1001L, newPosition);

        // 4. EnsureCaughtUpAsync lazy catch-up turn runs
        var subscriberNeuronState = new TestSubscriberNeuronState { SignalCursor = appState.SignalCursor };
        var handledPositions = new List<long>();

        var finalState = await signalLog.EnsureCaughtUpAsync(
            NeuronId.Parse($"{brain}/app/{subscriberId}"),
            subscriberNeuronState,
            (s, sig, _) =>
            {
                var testSig = (TestSignal)sig;
                Assert.Equal("live-signal-post-cutover", testSig.Message);
                return Task.FromResult(s with
                {
                    ProcessedPositions = [.. s.ProcessedPositions, 1001L]
                });
            },
            (s, _) =>
            {
                handledPositions.Add(s.SignalCursor);
                return Task.CompletedTask;
            },
            targetPosition: newPosition,
            ct: ct);

        // Crucial invariant check: 0 historical signals replayed; exactly 1 live signal processed!
        var processedPosition = Assert.Single(finalState.ProcessedPositions);
        Assert.Equal(1001L, processedPosition);
        Assert.Equal(1001L, finalState.SignalCursor);
    }

    [Fact]
    public async Task BrandNewSubscriber_InitializesToZero_WithoutError()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-new";
        var subscriberId = "app-sub-5";
        var publisherId = NeuronId.Parse("brain-new/chat/p5");

        var state = new AppState { SignalCursor = 0 };
        var persisted = false;
        var signalLog = new FakeBrainSignalLog(initialPosition: 200);
        var storage = new FakeGrainStorage();
        var registry = new FakeLegacyRegistry();

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            _ => { persisted = true; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(0L, result);
        Assert.Equal(0L, state.SignalCursor);
        Assert.False(persisted);
        Assert.Equal(1, storage.ReadStateCallCount);
        Assert.Equal(1, registry.GetCursorCallCount);
    }

    [Fact]
    public async Task TransientStorageFailure_GracefullyFallsThrough_WithoutCrashingTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-fault";
        var subscriberId = "app-sub-6";
        var publisherId = NeuronId.Parse("brain-fault/chat/p6");

        var state = new AppState { SignalCursor = 0 };
        var persisted = false;
        var signalLog = new FakeBrainSignalLog(initialPosition: 300);
        var storage = new FakeGrainStorage { ThrowOnRead = true };
        var registry = new FakeLegacyRegistry { ThrowOnQuery = true };
        var logger = new TestLogger();

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            _ => { persisted = true; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            logger: logger,
            legacyRegistryOverride: registry,
            ct: ct);

        // Graceful fall-through without crashing the turn
        Assert.Equal(0L, result);
        Assert.Equal(0L, state.SignalCursor);
        Assert.False(persisted);
        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains(logger.Warnings, w => w.Contains("Failed to read legacy SynapseState", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, w => w.Contains("Failed to query legacy synapse cursor registry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyStorageFailure_FallsThroughToRegistry_AndSeedsCutoverWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-storage-fail-reg-ok";
        var subscriberId = "app-sub-7";
        var publisherId = NeuronId.Parse("brain-storage-fail-reg-ok/chat/p7");

        var state = new AppState { SignalCursor = 0 };
        var persistedCount = 0;
        long committedCursor = -1;

        var signalLog = new FakeBrainSignalLog(initialPosition: 600);
        var storage = new FakeGrainStorage { ThrowOnRead = true };
        var registry = new FakeLegacyRegistry();
        registry.Cursors[(subscriberId, publisherId.ToString())] = 45;
        var logger = new TestLogger();

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            s => { persistedCount++; committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            logger: logger,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(600L, result);
        Assert.Equal(600L, state.SignalCursor);
        Assert.Equal(1, persistedCount);
        Assert.Equal(600L, committedCursor);
        Assert.Equal(1, signalLog.GetLatestPositionCallCount);
        Assert.Equal(1, storage.ReadStateCallCount);
        Assert.Equal(1, registry.GetCursorCallCount);
        Assert.Single(logger.Warnings);
        Assert.Contains("Failed to read legacy SynapseState", logger.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyStorageZeroCursor_FallsThroughToRegistry_AndSeedsCutoverWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-zero-storage";
        var subscriberId = "app-sub-8";
        var publisherId = NeuronId.Parse("brain-zero-storage/chat/p8");

        var state = new AppState { SignalCursor = 0 };
        var persistedCount = 0;
        long committedCursor = -1;

        var signalLog = new FakeBrainSignalLog(initialPosition: 800);
        var storage = new FakeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 0 }]
        };
        var registry = new FakeLegacyRegistry();
        registry.Cursors[(subscriberId, publisherId.ToString())] = 30;

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            s => { persistedCount++; committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(800L, result);
        Assert.Equal(800L, state.SignalCursor);
        Assert.Equal(1, persistedCount);
        Assert.Equal(800L, committedCursor);
        Assert.Equal(1, signalLog.GetLatestPositionCallCount);
        Assert.Equal(1, storage.ReadStateCallCount);
        Assert.Equal(1, registry.GetCursorCallCount);
    }

    [Fact]
    public async Task BothLegacyStorageAndRegistryZeroOrNull_DefaultsToZeroWithoutPersistence()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-both-zero";
        var subscriberId = "app-sub-9";
        var publisherId = NeuronId.Parse("brain-both-zero/chat/p9");

        var state = new AppState { SignalCursor = 0 };
        var persisted = false;

        var signalLog = new FakeBrainSignalLog(initialPosition: 900);
        var storage = new FakeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 0 }]
        };
        var registry = new FakeLegacyRegistry();
        registry.Cursors[(subscriberId, publisherId.ToString())] = 0; // zero cursor in registry too

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            _ => { persisted = true; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(0L, result);
        Assert.Equal(0L, state.SignalCursor);
        Assert.False(persisted);
        Assert.Equal(0, signalLog.GetLatestPositionCallCount);
        Assert.Equal(1, storage.ReadStateCallCount);
        Assert.Equal(1, registry.GetCursorCallCount);
    }

    [Fact]
    public async Task NegativeExistingSignalCursor_DoesNotBypassMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = "brain-neg-cursor";
        var subscriberId = "app-sub-10";
        var publisherId = NeuronId.Parse("brain-neg-cursor/chat/p10");

        var state = new AppState { SignalCursor = -1 };
        var persistedCount = 0;
        long committedCursor = -1;

        var signalLog = new FakeBrainSignalLog(initialPosition: 500);
        var storage = new FakeGrainStorage();
        storage.Storage[subscriberId] = new SynapseState
        {
            Sources = [new SynapseSource { Source = publisherId.ToString(), Cursor = 10 }]
        };
        var registry = new FakeLegacyRegistry();

        var result = await SubscriberCursorMigration.ResolveSubscriberCursorAsync(
            brain,
            subscriberId,
            publisherId,
            state,
            s => { persistedCount++; committedCursor = s.SignalCursor; return Task.CompletedTask; },
            signalLog,
            clusterStorage: storage,
            legacyRegistryOverride: registry,
            ct: ct);

        Assert.Equal(500L, result);
        Assert.Equal(500L, state.SignalCursor);
        Assert.Equal(1, persistedCount);
        Assert.Equal(500L, committedCursor);
    }
}

