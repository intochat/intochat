namespace DigitalBrain;

// The stream behind brain.On<T>: a hand-written enumerator, not a compiler iterator, because a
// consumer may dispose it while a read is in flight (a primed enumerator, a break, a failing
// handler). Disposal cancels the pending read, lets it settle, and only then releases the
// subscription, so an early exit never throws and never leaks the watch.
// One stream serves DigitalBrain.Client and DigitalBrain.Client.Orleans; the Orleans client passes no extra tokens.
internal sealed class WatchStream<T>(Func<CancellationToken, Task<(IAsyncEnumerable<T> Signals, IAsyncDisposable Handle)>> open,
    CancellationToken subscriptionCancellation, CancellationToken behaviorCancellation, Func<Task> onSubscribed) : IAsyncEnumerable<T>
{
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(open, cancellationToken, subscriptionCancellation, behaviorCancellation, onSubscribed);

    private sealed class Enumerator(Func<CancellationToken, Task<(IAsyncEnumerable<T> Signals, IAsyncDisposable Handle)>> open,
        CancellationToken cancellationToken, CancellationToken subscriptionCancellation, CancellationToken behaviorCancellation, Func<Task> onSubscribed) : IAsyncEnumerator<T>
    {
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, subscriptionCancellation, behaviorCancellation);
        private IAsyncEnumerator<T>? _signals;
        private IAsyncDisposable? _handle;
        private Task<bool>? _inFlight;
        private volatile bool _disposing;

        public T Current => _signals is { } signals ? signals.Current : default!;

        public ValueTask<bool> MoveNextAsync()
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            var move = MoveCoreAsync();
            _inFlight = move;
            return new(move);
        }

        private async Task<bool> MoveCoreAsync()
        {
            try
            {
                if (_signals is null)
                {
                    var (signals, handle) = await open(_lifetime.Token).ConfigureAwait(false);
                    _handle = handle;
                    _signals = signals.GetAsyncEnumerator(_lifetime.Token);
                    await onSubscribed().ConfigureAwait(false);
                }
                return await _signals.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposing)
            {
                // Disposal ended the read; the consumer asked for that, so it is not an error.
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposing) { return; }
            _disposing = true;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_inFlight is { } inFlight)
            {
                try { await inFlight.ConfigureAwait(false); }
                catch (Exception) { /* the read already reported to its awaiter; disposal only waits for it to settle */ }
            }
            if (_signals is { } signals)
            {
                try { await signals.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* a settled iterator disposes; a faulted one has nothing left to release */ }
            }
            if (_handle is { } handle) { await handle.DisposeAsync().ConfigureAwait(false); }
            _lifetime.Dispose();
        }
    }
}
