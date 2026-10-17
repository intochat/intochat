using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Client.Orleans;

internal sealed class SignalSubscription<T>(IClusterClient cluster, INeuron source, SubscriptionOptions options,
    ILogger logger, Action<IAsyncDisposable> closed, ILocalSignalHub? hub, CancellationToken cancellationToken)
    : ISignalSubscription<T>, INeuronObserver, ILocalSignalFaultSink where T : Signal
{
    // The waiting reader runs inside Nudge's pull, so a hub flush that follows this write has already
    // copied the signal onward. The publisher's own turn only queues; it does not run that reader.
    private readonly Channel<T> _messages = Channel.CreateBounded<T>(new BoundedChannelOptions(options.BufferCapacity) { SingleReader = true, AllowSynchronousContinuations = true });
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    // One pull at a time: the watermark only moves after a page's signals are in the channel.
    private readonly SemaphoreSlim _pull = new(1, 1);
    // Nudges before the starting read are ignored: the start covers them.
    private long _watermark = long.MaxValue;
    private INeuronObserver? _reference;
    private Task<Guid>? _watch;
    private Task _renewal = Task.CompletedTask;
    private Task? _cleanup;
    private int _reader;

    public Task Completion => _completion.Task;

    internal async Task ConnectAsync()
    {
        try
        {
            var token = _lifetime.Token;
            // Only the remote path renews; the caller's token must end a silo-local subscription too.
            token.Register(() => Finish(new OperationCanceledException(token)));
            // Subscribers start at the latest signal; the start is taken after the watch so nothing falls between.
            if (hub is not null)
            {
                hub.Subscribe(source.GetGrainId(), this);
                await StartAsync(token).ConfigureAwait(false);
            }
            else
            {
                _reference = cluster.CreateObjectReference<INeuronObserver>(this);
                await WatchAsync(token).ConfigureAwait(false);
                await StartAsync(token).ConfigureAwait(false);
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    _renewal = RenewAsync(token);
                }
            }
            logger.LogInformation("SubscriptionReady {Source} {SignalType}", source.GetGrainId(), typeof(T).Name);
        }
        catch (Exception error)
        {
            Finish(error);
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task<Guid> WatchAsync(CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            _watch = source.Watch(_reference!);
            return _watch.WaitAsync(options.OperationTimeout, token);
        }
    }

    private async Task StartAsync(CancellationToken token)
    {
        var start = await source.Read(0, 0).WaitAsync(options.OperationTimeout, token).ConfigureAwait(false);
        await _pull.WaitAsync(token).ConfigureAwait(false);
        try { _watermark = start.Last; }
        finally { _pull.Release(); }
    }

    // A reactivated neuron has forgotten its watchers; re-watching and pulling once recovers any nudge missed in the gap.
    private async Task RenewAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.RenewEvery, token).ConfigureAwait(false);
                await WatchAsync(token).ConfigureAwait(false);
                await PullAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception error) { Finish(error); }
        finally { await CleanupAsync().ConfigureAwait(false); }
    }

    async Task INeuronObserver.Nudge(SignalNudge nudge)
    {
        if (nudge.Sequence <= Volatile.Read(ref _watermark)) { return; }
        try { await PullAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Finish(error); }
    }

    // Reads pages after the watermark until the source has nothing newer, writing this subscription's signals.
    private async Task PullAsync(CancellationToken token)
    {
        await _pull.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var page = await source.Read(_watermark, options.PageSize).WaitAsync(options.OperationTimeout, token).ConfigureAwait(false);
                if (page.Floor > _watermark)
                {
                    logger.LogWarning("SubscriptionMissedSignals {Source} {SignalType} {Watermark} {Floor}", source.GetGrainId(), typeof(T).Name, _watermark, page.Floor);
                    _watermark = page.Floor;
                }
                if (page.Signals.Length == 0) { _watermark = Math.Max(_watermark, page.Last); return; }
                foreach (var signal in page.Signals)
                {
                    // A completed writer rejects late signals; only a live, full buffer is an overflow.
                    if (signal is T typed && !_messages.Writer.TryWrite(typed))
                    {
                        Finish(new InvalidOperationException("Signal subscription buffer overflowed."));
                        return;
                    }
                }
                Volatile.Write(ref _watermark, page.Sequences[^1]);
                if (page.Last <= _watermark) { return; }
            }
        }
        finally { _pull.Release(); }
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reader, 1) != 0) { throw new InvalidOperationException("A subscription has one reader."); }
        try
        {
            await foreach (var signal in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            { yield return signal; }
        }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }

    void ILocalSignalFaultSink.OnError(Exception error) => Finish(error);

    private void Finish(Exception? error)
    {
        lock (_gate)
        {
            if (Completion.IsCompleted) { return; }
            _messages.Writer.TryComplete(error);
            if (error is OperationCanceledException canceled) { _completion.TrySetCanceled(canceled.CancellationToken); }
            else if (error is not null)
            {
                _completion.TrySetException(error);
                // Observed here so an unread failure never surfaces as an unobserved task exception.
                _ = _completion.Task.Exception;
            }
            else { _completion.TrySetResult(); }
            _lifetime.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Finish(null);
        await _renewal.ConfigureAwait(false);
        await CleanupAsync().ConfigureAwait(false);
    }

    private Task CleanupAsync()
    {
        // Cleanup runs off the gate: disposing the lifetime waits for a running cancellation callback, which takes the gate.
        lock (_gate) { return _cleanup ??= Task.Run(CleanupCoreAsync); }
    }

    private async Task CleanupCoreAsync()
    {
        try
        {
            hub?.Unsubscribe(source.GetGrainId(), this);
            if (_reference is null) { return; }
            if (_watch is { IsCompleted: false }) { _ = CleanupLateWatchAsync(_watch); }
            else { _ = _watch?.Exception; }
            try { await UnwatchAsync().ConfigureAwait(false); }
            finally { cluster.DeleteObjectReference<INeuronObserver>(_reference); }
        }
        finally
        {
            _lifetime.Dispose();
            closed(this);
        }
    }

    private async Task CleanupLateWatchAsync(Task<Guid> watch)
    {
        try { await watch.ConfigureAwait(false); }
        catch { return; }
        await UnwatchAsync().ConfigureAwait(false);
    }

    private async Task UnwatchAsync()
    {
        try { await source.Unwatch(_reference!).WaitAsync(options.OperationTimeout).ConfigureAwait(false); }
        catch (Exception error) { logger.LogWarning(error, "Subscription cleanup failed; remote membership expires with its lease."); }
    }
}
