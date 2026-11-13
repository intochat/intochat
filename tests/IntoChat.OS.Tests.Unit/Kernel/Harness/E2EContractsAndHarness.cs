#pragma warning disable xUnit1051
using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.SystemOne;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace IntoChat.OS.Kernel.Tests.Unit.Harness;


#region F20: Reusable Testing Framework for Agent Verification (Delegates to Production TestVerificationService)

public sealed class InMemoryTestVerificationService : ITestVerificationService
{
    private readonly TestVerificationService _inner = new(new E2ETestVerificationNeuron());

    public Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.RunTargetedSuiteAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testSuiteId);
        if (string.IsNullOrWhiteSpace(testSuiteId))
        {
            throw new ArgumentException("TestSuiteId cannot be empty or whitespace.", nameof(testSuiteId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.GetHistoricalRunsAsync(testSuiteId, cancellationToken);
    }

    private sealed class E2ETestVerificationNeuron : ITestVerificationNeuron
    {
        private readonly ConcurrentDictionary<string, List<TestResultSummary>> _history = new(StringComparer.OrdinalIgnoreCase);

        public Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new TestExecutionResult(
                Success: true,
                Total: 10,
                Passed: 10,
                Failed: 0,
                Skipped: 0,
                DurationMs: 42,
                Summary: "All tests executed successfully.",
                Failures: []);

            var list = _history.GetOrAdd(request.SuiteName, static _ => []);
            lock (list)
            {
                list.Add(new TestResultSummary(
                    RunId: $"run_{list.Count + 1}",
                    SuiteName: request.SuiteName,
                    ExecutedAt: DateTimeOffset.UtcNow,
                    Success: true,
                    Total: 10,
                    Passed: 10,
                    Failed: 0,
                    DurationMs: 42));
            }
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(testSuiteId);
            cancellationToken.ThrowIfCancellationRequested();
            if (_history.TryGetValue(testSuiteId, out var existing) && existing.Count > 0)
            {
                lock (existing)
                {
                    return Task.FromResult<IReadOnlyList<TestResultSummary>>(existing.ToList());
                }
            }

            return Task.FromResult<IReadOnlyList<TestResultSummary>>(
            [
                new TestResultSummary(
                    RunId: "run_1",
                    SuiteName: testSuiteId,
                    ExecutedAt: DateTimeOffset.UtcNow,
                    Success: true,
                    Total: 10,
                    Passed: 10,
                    Failed: 0,
                    DurationMs: 42)
            ]);
        }
    }
}

#endregion

#region F21: Aspire Telemetry Verification & Flush Barrier (Exercises TelemetryExportBarrier OpenTelemetry Pipeline)

public sealed record TelemetryMetricSnapshot(string MetricName, double Value, DateTimeOffset Timestamp);

public sealed class TelemetryFlushBarrier : IDisposable
{
    private static readonly ActivitySource BarrierSource = new(TelemetryExportBarrier.SourceName);
    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;
    private readonly ConcurrentBag<TelemetryMetricSnapshot> _metrics = [];
    private long _flushCount;

    public TelemetryFlushBarrier()
    {
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(TelemetryExportBarrier.SourceName)
            .Build();
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .Build();
    }

    public long FlushCount => Interlocked.Read(ref _flushCount);

    public void RecordMetric(string name, double value)
    {
        _metrics.Add(new TelemetryMetricSnapshot(name, value, DateTimeOffset.UtcNow));
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var marker = Guid.NewGuid();
        using (var activity = BarrierSource.StartActivity(
            TelemetryExportBarrier.MarkerName,
            ActivityKind.Internal,
            new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)))
        {
            activity?.SetTag(TelemetryExportBarrier.MarkerAttribute, marker.ToString("D"));
        }

        _tracerProvider.ForceFlush(5_000);
        _meterProvider.ForceFlush(5_000);
        var receipt = new TelemetryExportReceipt(marker, DateTimeOffset.UtcNow);
        if (receipt.Marker != Guid.Empty)
        {
            Interlocked.Increment(ref _flushCount);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<TelemetryMetricSnapshot> QueryMetrics(string name)
    {
        return _metrics.Where(m => string.Equals(m.MetricName, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Dispose()
    {
        _tracerProvider.Dispose();
        _meterProvider.Dispose();
    }
}

#endregion
