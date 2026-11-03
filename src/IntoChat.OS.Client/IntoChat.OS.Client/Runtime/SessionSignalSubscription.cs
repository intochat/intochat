using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Signals.Watch;
using IntoChat.OS;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client.Runtime;

// brain.On<T> over the session's one stream: registers the durable subscription first (a refusal
// surfaces here), then yields each batch's signals and acknowledges the batch once the consumer asks
// for the next one, so a consumer that dies mid-batch is resent the whole batch. Another brain.On<T> of
// the same neuron and signal in this behavior shares the subscription and still reads every batch.
// T is an os Signal, or (06-07, transitional) a plain neuron's [Signal] record, which the runtime names by its alias.
internal sealed class SessionSignalSubscription<T> : IAsyncDisposable where T : class
{
    private readonly SharedSubscription.Reader _reader;
    private readonly BehaviorTrace _trace;
    private readonly Action<T, CapabilityCaller>? _onProvenance;

    private SessionSignalSubscription(SharedSubscription.Reader reader, BehaviorTrace trace, Action<T, CapabilityCaller>? onProvenance)
    {
        _reader = reader;
        _trace = trace;
        _onProvenance = onProvenance;
    }

    public static async Task<SessionSignalSubscription<T>> OpenAsync(SessionConnection session, string behavior, NeuronProxy source, BehaviorTrace trace, CancellationToken cancellationToken, string? selector = null, Action<T, CapabilityCaller>? onProvenance = null)
    {
        var signal = typeof(DigitalBrain.Signal).IsAssignableFrom(typeof(T))
            ? typeof(T).GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias
                ?? throw new ArgumentException($"{typeof(T).Name} is not a [Signal] record.")
            : typeof(T).Name;
        var id = await session.SubscribeAsync(new SubscribeReport(behavior, source.Contract.FullName!, source.Key, signal, selector), cancellationToken).ConfigureAwait(false);
        return new SessionSignalSubscription<T>(session.Join(id), trace, onProvenance);
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in _reader.Batches.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // The behavior's reaction to these signals continues the trace that published them.
            if (batch.TraceParent is { } traceParent) { _trace.TraceParent = traceParent; }
            // The elapsed time while this batch was handled measures its execution cost;
            // the ack report carries it so the brain can price and record the
            // synapse run against the batch's trace without per-batch OS query allocations.
            var wallStart = Stopwatch.GetTimestamp();
            var skipped = batch.Signals.Length > 0;
            foreach (var json in batch.Signals)
            {
                var signal = JsonSerializer.Deserialize<T>(json, RuntimeProtocol.Json)!;
                if (_onProvenance is not null && TryReadProvenance(json) is { } caller)
                {
                    _onProvenance(signal, caller);
                }
                yield return signal;
                skipped &= SignalDeliveries.WasSkipped(signal);
            }
            // Resuming here means the consumer asked for more: every signal of the batch was handled.
            var wallMicros = (long)Stopwatch.GetElapsedTime(wallStart).TotalMicroseconds;
            var cpuMicros = wallMicros;
            await _reader.AcknowledgeAsync(batch.Watermark, cpuMicros, wallMicros, skipped, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CapabilityCaller? TryReadProvenance(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("provenance", out var prop)
                && prop.ValueKind == JsonValueKind.Object)
            {
                return prop.Deserialize<CapabilityCaller>(RuntimeProtocol.Json);
            }
        }
        catch (JsonException error)
        {
            _ = error.Message;
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _reader.Leave();
        return ValueTask.CompletedTask;
    }
}
