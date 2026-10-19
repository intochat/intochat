using System.Collections.Concurrent;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Runtime.InMemory.Tests.Unit;

// Neuron.WatchRenewing on the in-memory runtime and a FakeTimeProvider: renewal period is a fifth of the 5 minute lease.
public sealed class WatchRenewalFacts
{
    private static readonly TimeSpan Renewal = TimeSpan.FromMinutes(1);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReactivatedSourceIsReportedOnTheNextRenewal()
    {
        var (runtime, time, _) = Create();
        await using var _ = runtime;
        var key = $"brain-1/lease/{Guid.NewGuid():N}";
        var holder = runtime.ResolveKey<ILeaseHolder>("brain-1/holder");
        await holder.Hold(key).WaitAsync(_ct);

        await runtime.ResolveKey<ILeaseSource>(key).Deactivate().WaitAsync(_ct);
        time.Advance(Renewal);
        await runtime.Idle().WaitAsync(_ct);

        Assert.Equal(1, await holder.Reactivations().WaitAsync(_ct));
    }

    [Fact]
    public async Task AFailedRenewalLogsAWarningAndTheNextOneRecovers()
    {
        var (runtime, time, logs) = Create();
        await using var _ = runtime;
        var key = $"brain-1/lease/{Guid.NewGuid():N}";
        var holder = runtime.ResolveKey<ILeaseHolder>("brain-1/holder");
        await holder.Hold(key).WaitAsync(_ct);

        LeaseSource.Fail(key, 1);
        time.Advance(Renewal);
        await runtime.Idle().WaitAsync(_ct);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Error is InvalidOperationException);

        await runtime.ResolveKey<ILeaseSource>(key).Deactivate().WaitAsync(_ct);
        time.Advance(Renewal);
        await runtime.Idle().WaitAsync(_ct);
        Assert.Equal(1, await holder.Reactivations().WaitAsync(_ct));
    }

    [Fact]
    public async Task ADisposedRenewalStopsRenewing()
    {
        var (runtime, time, _) = Create();
        await using var _ = runtime;
        var key = $"brain-1/lease/{Guid.NewGuid():N}";
        var holder = runtime.ResolveKey<ILeaseHolder>("brain-1/holder");
        await holder.Hold(key).WaitAsync(_ct);
        await holder.Release().WaitAsync(_ct);

        await runtime.ResolveKey<ILeaseSource>(key).Deactivate().WaitAsync(_ct);
        time.Advance(Renewal + Renewal);
        await runtime.Idle().WaitAsync(_ct);

        Assert.Equal(0, await holder.Reactivations().WaitAsync(_ct));
    }

    private static (InMemoryRuntime Runtime, FakeTimeProvider Time, CapturedLogs Logs) Create()
    {
        var time = new FakeTimeProvider();
        var logs = new CapturedLogs();
        var runtime = InMemoryRuntime.Create(time, builder =>
        {
            builder.AddNeuron<LeaseSource>().AddNeuron<LeaseHolder>();
            builder.Services.AddSingleton<ILoggerFactory>(logs);
        });
        return (runtime, time, logs);
    }

    private sealed class CapturedLogs : ILoggerFactory
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Error)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<(LogLevel, Exception?)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, exception));
        }
    }
}

[Alias("tests.inmemory.lease-source")]
public interface ILeaseSource : INeuron
{
    Task Deactivate();
}

// A source whose watch can be made to fail a number of times.
public sealed class LeaseSource : Neuron, ILeaseSource
{
    private static readonly ConcurrentDictionary<string, int> Failures = new(StringComparer.Ordinal);

    public static void Fail(string key, int times) => Failures[key] = times;

    public override Task<Guid> Watch(INeuronObserver observer)
    {
        if (Failures.TryGetValue(Context.Key, out var left) && left > 0 && Failures.TryUpdate(Context.Key, left - 1, left))
        { throw new InvalidOperationException("The source is unavailable."); }
        return base.Watch(observer);
    }

    public Task Deactivate()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }
}

[Alias("tests.inmemory.lease-holder")]
public interface ILeaseHolder : INeuron
{
    Task Hold(string source);
    Task Release();
    Task<int> Reactivations();
}

public sealed class LeaseHolder : Neuron, ILeaseHolder, INeuronObserver
{
    private IWatchRenewal? _lease;
    private int _reactivations;

    public async Task Hold(string source)
        => _lease = await WatchRenewing(Context.Resolver.ResolveKey<ILeaseSource>(source), null, keepAlive: false,
            onSourceReactivated: _ => { _reactivations++; return Task.CompletedTask; });

    public async Task Release()
    {
        if (_lease is not null) { await _lease.DisposeAsync(); }
    }

    public Task<int> Reactivations() => Task.FromResult(_reactivations);

    public Task Nudge(SignalNudge nudge) => Task.CompletedTask;
}
