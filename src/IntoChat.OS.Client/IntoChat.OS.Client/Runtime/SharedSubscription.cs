using System.Threading.Channels;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client.Runtime;

// The brain keeps one durable subscription per behavior, neuron and signal type, so every reader a behavior
// opens on the same neuron's signal shares its id and its watermark. Each reader is handed every batch the
// brain still holds unacknowledged when it joins and every batch after; the brain is told a watermark only
// once every reader has acknowledged it. A batch that arrives before any reader joins waits for the first.
internal sealed class SharedSubscription(SessionConnection session, string id)
{
    private readonly Lock _gate = new();
    private readonly List<Reader> _readers = [];
    // Batches handed out and not yet reported acknowledged, oldest first: where a joining reader starts.
    private readonly List<SignalBatchEvent> _unacknowledged = [];
    // The highest signal number queued; anything at or below it is a resend.
    private long _queued;
    private long _reported;
    private long _confirmed;
    // What readers spent on batches the brain has not been told about; the report that advances carries it.
    private long _cpuMicroseconds;
    private long _wallMicroseconds;
    // The batches, by watermark, some reader handled and the brain has not been told about. A report covers the
    // batches at or below its watermark; it is a skip when no reader handled any of them.
    private readonly HashSet<long> _handled = [];
    // While paused (a reassign is draining this behavior) arriving batches are kept back, not handed to readers; resumed, they are
    // handed over in order. _delivered is the highest watermark handed to a reader, so idle means every reader acknowledged it.
    private readonly List<SignalBatchEvent> _held = [];
    private bool _paused;
    private long _delivered;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Reader Join()
    {
        lock (_gate)
        {
            var reader = new Reader(this, _reported);
            foreach (var batch in _unacknowledged.Where(batch => !_held.Contains(batch)))
            {
                reader.Queue.Writer.TryWrite(batch);
                _delivered = Math.Max(_delivered, batch.Watermark);
            }
            _readers.Add(reader);
            return reader;
        }
    }

    public void Arrive(SignalBatchEvent batch)
    {
        lock (_gate)
        {
            var first = batch.Watermark - batch.Signals.Length + 1;
            var fresh = batch.Signals.Skip((int)Math.Clamp(_queued - first + 1, 0, batch.Signals.Length)).ToArray();
            if (fresh.Length == 0) { return; }
            _queued = batch.Watermark;
            var arrived = batch with { Signals = fresh };
            _unacknowledged.Add(arrived);
            if (_paused) { _held.Add(arrived); return; }
            Deliver(arrived);
        }
    }

    // Under the gate.
    private void Deliver(SignalBatchEvent batch)
    {
        foreach (var reader in _readers) { reader.Queue.Writer.TryWrite(batch); }
        _delivered = Math.Max(_delivered, batch.Watermark);
    }

    // Stops handing batches to readers. The task completes once every batch already handed over is acknowledged.
    public Task Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SettleIdle();
            return _idle.Task;
        }
    }

    // The drain was abandoned: what was kept back goes to the readers now, in order.
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            var held = _held.ToArray();
            _held.Clear();
            foreach (var batch in held) { Deliver(batch); }
        }
    }

    // Under the gate.
    private void SettleIdle()
    {
        if (_paused && (_readers.Count == 0 || _readers.Min(reader => reader.Acknowledged) >= _delivered)) { _idle.TrySetResult(); }
    }

    public void Complete()
    {
        lock (_gate) { foreach (var reader in _readers) { reader.Queue.Writer.TryComplete(); } }
    }

    private Task AcknowledgeAsync(Reader reader, long watermark, long cpuMicroseconds, long wallMicroseconds, bool skipped, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Report? report;
        lock (_gate)
        {
            if (reader.Left) { return Task.CompletedTask; }
            reader.Acknowledged = Math.Max(reader.Acknowledged, watermark);
            _cpuMicroseconds += cpuMicroseconds;
            _wallMicroseconds += wallMicroseconds;
            if (!skipped && watermark > _reported) { _handled.Add(watermark); }
            report = Advance();
            SettleIdle();
        }
        return report is { } advanced ? SendAsync(advanced, cancellationToken) : Task.CompletedTask;
    }

    // A reader that leaves mid-batch never acknowledged it. With others left, the watermark follows them;
    // with none, the brain still holds whatever is unconfirmed and resends it to the next reader.
    private void Leave(Reader reader)
    {
        Report? report;
        lock (_gate)
        {
            if (reader.Left) { return; }
            reader.Left = true;
            _readers.Remove(reader);
            reader.Queue.Writer.TryComplete();
            if (_readers.Count == 0)
            {
                _unacknowledged.Clear();
                _handled.Clear();
                _held.Clear();
                _queued = _reported = _confirmed;
                SettleIdle();
                return;
            }
            report = Advance();
            SettleIdle();
        }
        if (report is { } advanced) { _ = SendQuietlyAsync(advanced); }
    }

    // Under the gate: the lowest watermark every reader acknowledged, when it moved past the last report.
    private Report? Advance()
    {
        var watermark = _readers.Min(reader => reader.Acknowledged);
        if (watermark <= _reported) { return null; }
        var batches = _unacknowledged.RemoveAll(batch => batch.Watermark <= watermark);
        _reported = watermark;
        var report = new Report(watermark, _cpuMicroseconds, _wallMicroseconds, batches, _handled.RemoveWhere(handled => handled <= watermark) == 0);
        _cpuMicroseconds = _wallMicroseconds = 0;
        return report;
    }

    private async Task SendAsync(Report report, CancellationToken cancellationToken)
    {
        await session.AcknowledgeAsync(id, report.Watermark, cancellationToken, report.CpuMicroseconds, report.WallMicroseconds, report.Batches, report.Skipped).ConfigureAwait(false);
        lock (_gate) { _confirmed = Math.Max(_confirmed, report.Watermark); }
    }

    private async Task SendQuietlyAsync(Report report)
    {
        // The brain resends what it still holds; a later acknowledgement covers a failed send.
        await Task.WhenAny(SendAsync(report, CancellationToken.None)).ConfigureAwait(false);
    }

    private readonly record struct Report(long Watermark, long CpuMicroseconds, long WallMicroseconds, int Batches, bool Skipped);

    // One brain.On<T> enumeration's view of the shared subscription; Acknowledged is guarded by the gate.
    public sealed class Reader
    {
        private readonly SharedSubscription _subscription;

        internal Reader(SharedSubscription subscription, long acknowledged)
        {
            _subscription = subscription;
            Acknowledged = acknowledged;
        }

        internal Channel<SignalBatchEvent> Queue { get; } = Channel.CreateUnbounded<SignalBatchEvent>(new() { SingleReader = true });
        internal long Acknowledged { get; set; }
        internal bool Left { get; set; }

        public ChannelReader<SignalBatchEvent> Batches => Queue.Reader;

        public Task AcknowledgeAsync(long watermark, long cpuMicroseconds, long wallMicroseconds, bool skipped, CancellationToken cancellationToken)
            => _subscription.AcknowledgeAsync(this, watermark, cpuMicroseconds, wallMicroseconds, skipped, cancellationToken);

        public void Leave() => _subscription.Leave(this);
    }
}
