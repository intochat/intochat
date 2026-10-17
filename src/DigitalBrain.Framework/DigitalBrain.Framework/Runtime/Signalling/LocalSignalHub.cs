using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Framework;

public sealed class LocalSignalHub(ILogger<LocalSignalHub> logger) : ILocalSignalHub
{
    private readonly ConcurrentDictionary<(GrainId, INeuronObserver), Delivery> _observers = new();
    private readonly SiloSignalBus _silo = new();
    public Subscription<T> Subscribe<T>() where T : Signal => _silo.Subscribe<T>();
    public void Publish(Signal signal) => _silo.Publish(signal);
    public sealed class Subscription<T>(ChannelReader<T> reader, Action close) : IDisposable where T : Signal
    {
        public ChannelReader<T> Reader { get; } = reader;
        public void Dispose() => close();
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
        if (!_observers.TryAdd((source, observer), delivery)) { delivery.Close(); }
    }
    public void Unsubscribe(GrainId source, INeuronObserver observer)
    {
        if (_observers.TryRemove((source, observer), out var delivery)) { delivery.Close(); }
    }
    public void Publish(GrainId source, Signal signal)
    {
        _silo.Publish(signal);
        foreach (var entry in _observers)
        { if (entry.Key.Item1 == source) { entry.Value.Post(signal); } }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
        => Task.WhenAll(_observers.Values.Select(delivery => delivery.Idle(cancellationToken)));

    private sealed class Delivery
    {
        private readonly Channel<Signal> _queue = Channel.CreateBounded<Signal>(new BoundedChannelOptions(1024) { SingleReader = true, AllowSynchronousContinuations = false });
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Action<Exception> _failed;
        private readonly Lock _idle = new();
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pending;
        private int _closed;
        public Delivery(INeuronObserver observer, Action<Exception> failed)
        { _failed = failed; _ = Pump(observer); }
        public void Post(Signal signal)
        {
            if (Volatile.Read(ref _closed) != 0) { return; }
            lock (_idle) { _pending++; }
            if (Volatile.Read(ref _closed) != 0 || !_queue.Writer.TryWrite(signal))
            {
                FinishOne();
                if (Volatile.Read(ref _closed) == 0)
                { _failed(new InvalidOperationException("Local observer buffer overflowed.")); }
            }
        }
        public Task Idle(CancellationToken cancellationToken)
        {
            lock (_idle)
            { return _pending == 0 ? Task.CompletedTask : _drained.Task.WaitAsync(cancellationToken); }
        }
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) { return; }
            _queue.Writer.TryComplete();
            _lifetime.Cancel();
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
                await foreach (var signal in _queue.Reader.ReadAllAsync(_lifetime.Token))
                {
                    try { await observer.OnSignalAsync(signal).WaitAsync(_lifetime.Token); }
                    finally { FinishOne(); }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _failed(error); }
            // Close can race a completing pump; keep the tiny CTS until this delivery is collected.
        }
    }
}
