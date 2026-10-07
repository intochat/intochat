using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Client.Runtime;

// brain.On<T> over the session's one stream: registers the durable subscription first (a refusal
// surfaces here), then yields each batch's signals and acknowledges the batch once the consumer asks
// for the next one, so a consumer that dies mid-batch is resent the whole batch.
internal sealed class SessionSignalSubscription<T> : IAsyncDisposable where T : Signal
{
    private readonly SessionConnection _session;
    private readonly string _id;
    private readonly BehaviorTrace _trace;
    private long _acknowledged;

    private SessionSignalSubscription(SessionConnection session, string id, BehaviorTrace trace)
    {
        _session = session;
        _id = id;
        _trace = trace;
    }

    public static async Task<SessionSignalSubscription<T>> OpenAsync(SessionConnection session, string behavior, NeuronProxy source, BehaviorTrace trace, CancellationToken cancellationToken)
    {
        var id = await session.SubscribeAsync(new SubscribeReport(behavior, source.Contract.FullName!, source.Key, typeof(T).Name), cancellationToken).ConfigureAwait(false);
        return new SessionSignalSubscription<T>(session, id, trace);
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in _session.Batches(_id).ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // The behavior's reaction to these signals continues the trace that published them.
            if (batch.TraceParent is { } traceParent) { _trace.TraceParent = traceParent; }
            foreach (var json in batch.Signals) { yield return JsonSerializer.Deserialize<T>(json, RuntimeProtocol.Json)!; }
            // Resuming here means the consumer asked for more: every signal of the batch was handled.
            await _session.AcknowledgeAsync(_id, batch.Watermark, cancellationToken).ConfigureAwait(false);
            _acknowledged = batch.Watermark;
        }
    }

    public ValueTask DisposeAsync()
    {
        _session.Release(_id, _acknowledged);
        return ValueTask.CompletedTask;
    }
}
