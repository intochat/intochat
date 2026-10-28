using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Signalling;

namespace IntoChat.OS.Testing;

// SignalProbe for a plain neuron's [Signal] records (06-04): the same NextAsync/Snapshot surface, fed by brain.On<T> through
// the session synapse neuron. The subscription is registered before Observe returns, so nothing published after it is missed.
public sealed class PlainSignalProbe<T> : IAsyncDisposable where T : DigitalBrain.Signal
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Channel<T> _pending;
    private readonly Queue<T> _recent = new();
    private readonly Lock _gate = new();
    private readonly int _buffer;
    private readonly TestExecutionOptions _execution;
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _worker = Task.CompletedTask;
    private int _disposed;

    private PlainSignalProbe(int buffer, TestExecutionOptions execution)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buffer);
        _buffer = buffer;
        _execution = execution;
        _pending = Channel.CreateBounded<T>(buffer);
    }

    internal static async Task<PlainSignalProbe<T>> Start(ISignalWatch brain, object source, int buffer, TestExecutionOptions execution, CancellationToken cancellationToken)
    {
        var probe = new PlainSignalProbe<T>(buffer, execution);
        probe._worker = probe.ObserveAsync(brain.On<T>(source, () => { probe._subscribed.TrySetResult(); return Task.CompletedTask; }, probe._cancel.Token));
        await Task.WhenAny(probe._subscribed.Task, probe._worker).WaitAsync(execution.AssertionTimeout, cancellationToken).ConfigureAwait(false);
        if (probe._worker.IsFaulted) { await probe._worker.ConfigureAwait(false); }
        return probe;
    }

    // Every signal of the given [Signal] types source publishes, in publication order (06-06d: what Observe<Signal> gave for a
    // grain neuron). One session on the session synapse neuron holds the synapse for all of them.
    internal static async Task<PlainSignalProbe<T>> StartAll(IServiceProvider services, object source, IReadOnlyList<Type> types, int buffer,
        TestExecutionOptions execution, CancellationToken cancellationToken)
    {
        var target = source is DigitalBrain.Invocation.NeuronProxy proxy ? proxy.Target
            : throw new ArgumentException("Observe a plain neuron obtained from this brain's Get.", nameof(source));
        var byAlias = types.ToDictionary(SignalLog.Alias, StringComparer.Ordinal);
        var probe = new PlainSignalProbe<T>(buffer, execution);
        var session = SiloNeurons.Get<ISessionSynapses>(services, SessionSynapses.For(target.Brain, "client/" + Guid.NewGuid().ToString("N")));
        await session.Subscribe(target, [.. byAlias.Keys]).WaitAsync(execution.AssertionTimeout, cancellationToken).ConfigureAwait(false);
        probe._subscribed.TrySetResult();
        probe._worker = probe.ObserveAsync(ReadAll(session, byAlias, probe._cancel.Token));
        return probe;
    }

    private static async IAsyncEnumerable<T> ReadAll(ISessionSynapses session, Dictionary<string, Type> byAlias,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long acked = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SessionBatch? batch;
                try { batch = await session.Next(acked, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { yield break; }
                if (batch is null) { continue; }
                foreach (var entry in batch.Signals)
                {
                    if (byAlias.TryGetValue(entry.Alias, out var type))
                    { yield return (T)JsonSerializer.Deserialize(entry.Payload, type, JsonSerializerOptions.Web)!; }
                }
                acked = batch.Number;
            }
        }
        finally
        {
            try { await session.End().ConfigureAwait(false); }
            catch (Exception) { /* the session expires after the runtime's SessionTtl */ }
        }
    }

    public IReadOnlyList<T> Snapshot { get { lock (_gate) { return _recent.ToArray(); } } }

    private async Task ObserveAsync(IAsyncEnumerable<T> signals)
    {
        try
        {
            await foreach (var value in signals.WithCancellation(_cancel.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_recent.Count == _buffer) { _recent.Dequeue(); }
                    _recent.Enqueue(value);
                }
                if (!_pending.Writer.TryWrite(value)) { throw new InvalidOperationException("Signal probe buffer overflowed."); }
            }
            _pending.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { _pending.Writer.TryComplete(); }
        catch (Exception error) { _pending.Writer.TryComplete(error); _subscribed.TrySetException(error); }
    }

    public async Task<T> NextAsync(Func<T, bool>? predicate = null, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_execution.AssertionTimeout);
        try
        {
            while (true)
            {
                var next = await _pending.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (predicate is null || predicate(next)) { return next; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No matching {typeof(T).Name} within {_execution.AssertionTimeout}; observed {Snapshot.Count} facts (payloads omitted).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await _cancel.CancelAsync().ConfigureAwait(false);
        try { await _worker.WaitAsync(_execution.CleanupTimeout).ConfigureAwait(false); }
        finally { _cancel.Dispose(); }
    }
}
