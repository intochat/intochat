using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// The silo's one local delivery point: nudges to in-process observers of a source, looked up by source, and
// signals to typed in-process listeners.
public sealed class LocalSignalHub(ILogger<LocalSignalHub> logger) : ILocalSignalHub
{
    private readonly Lock _gate = new();
    private readonly Dictionary<GrainId, List<Delivery>> _observers = [];
    private readonly HashSet<Action<Signal>> _listeners = [];

    public sealed class Subscription<T>(ChannelReader<T> reader, Action close) : IDisposable where T : Signal
    {
        public ChannelReader<T> Reader { get; } = reader;
        public void Dispose() => close();
    }

    public Subscription<T> Subscribe<T>() where T : Signal
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1024) { SingleReader = true, AllowSynchronousContinuations = false });
        void Close() { lock (_gate) { _listeners.Remove(Receive); } channel.Writer.TryComplete(); }
        void Receive(Signal signal)
        {
            if (signal is T value && !channel.Writer.TryWrite(value))
            {
                channel.Writer.TryComplete(new InvalidOperationException("Silo signal buffer overflowed."));
                Close();
            }
        }
        lock (_gate) { _listeners.Add(Receive); }
        return new(channel.Reader, Close);
    }

    public void Publish(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        Action<Signal>[] listeners;
        lock (_gate) { listeners = [.. _listeners]; }
        foreach (var receive in listeners) { receive(signal); }
    }

    public void Subscribe(GrainId source, INeuronObserver observer)
    {
        var delivery = new Delivery(observer, error =>
        {
            logger.LogWarning(error, "Local signal observer failed for {Source}.", source);
            Unsubscribe(source, observer);
            if (observer is ILocalSignalFaultSink sink)
            {
                try { sink.OnError(error); }
                catch (Exception notificationError) { logger.LogWarning(notificationError, "Observer fault notification failed for {Source}.", source); }
            }
        });
        lock (_gate)
        {
            if (!_observers.TryGetValue(source, out var deliveries)) { _observers[source] = deliveries = []; }
            if (!deliveries.Exists(existing => existing.Observer.Equals(observer))) { deliveries.Add(delivery); return; }
        }
        delivery.Close();
    }

    public void Unsubscribe(GrainId source, INeuronObserver observer)
    {
        Delivery? removed = null;
        lock (_gate)
        {
            if (_observers.TryGetValue(source, out var deliveries))
            {
                var index = deliveries.FindIndex(existing => existing.Observer.Equals(observer));
                if (index >= 0) { removed = deliveries[index]; deliveries.RemoveAt(index); }
                if (deliveries.Count == 0) { _observers.Remove(source); }
            }
        }
        removed?.Close();
    }

    public void Publish(GrainId source, SignalNudge nudge)
    {
        ArgumentNullException.ThrowIfNull(nudge);
        Delivery[] deliveries;
        lock (_gate)
        {
            if (!_observers.TryGetValue(source, out var registered)) { return; }
            deliveries = [.. registered];
        }
        foreach (var delivery in deliveries) { delivery.Post(nudge); }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        Delivery[] deliveries;
        lock (_gate) { deliveries = [.. _observers.Values.SelectMany(registered => registered)]; }
        return Task.WhenAll(deliveries.Select(delivery => delivery.Idle(cancellationToken)));
    }

    // Nudges are idempotent: when the observer is a full buffer behind, a newer nudge is dropped because the
    // observer's next read after any queued nudge covers it.
    private sealed class Delivery
    {
        private readonly Channel<SignalNudge> _queue = Channel.CreateBounded<SignalNudge>(new BoundedChannelOptions(1024) { SingleReader = true, AllowSynchronousContinuations = false });
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Action<Exception> _failed;
        private readonly Lock _idle = new();
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pending;
        private int _closed;
        public Delivery(INeuronObserver observer, Action<Exception> failed)
        { Observer = observer; _failed = failed; _ = Pump(observer); }
        public INeuronObserver Observer { get; }
        public void Post(SignalNudge nudge)
        {
            if (Volatile.Read(ref _closed) != 0) { return; }
            lock (_idle) { _pending++; }
            if (Volatile.Read(ref _closed) != 0 || !_queue.Writer.TryWrite(nudge)) { FinishOne(); }
        }
        public Task Idle(CancellationToken cancellationToken)
        {
            // A closed delivery never delivers what is still queued, so it counts as idle.
            lock (_idle)
            { return _pending == 0 || Volatile.Read(ref _closed) != 0 ? Task.CompletedTask : _drained.Task.WaitAsync(cancellationToken); }
        }
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) { return; }
            _queue.Writer.TryComplete();
            _lifetime.Cancel();
            // Releases a flush that began while nudges were queued: they are dropped, never finished.
            lock (_idle) { _drained.TrySetResult(); }
        }
        private void FinishOne()
        {
            lock (_idle)
            {
                _pending--;
                if (_pending == 0)
                {
                    _drained.TrySetResult();
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        private async Task Pump(INeuronObserver observer)
        {
            try
            {
                await foreach (var nudge in _queue.Reader.ReadAllAsync(_lifetime.Token))
                {
                    try { await observer.Nudge(nudge).WaitAsync(_lifetime.Token); }
                    finally { FinishOne(); }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _failed(error); }
            // Close can race a completing pump; keep the tiny CTS until this delivery is collected.
        }
    }
}
