using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

internal sealed record StoredRecord(byte[] Record, long ETagSeq);

// A snapshot that failed before its head commit: the head is unchanged, so nothing about the log is ambiguous.
internal sealed class SnapshotNotWrittenException(PartId part, int bytes, Exception inner) : Exception($"Part {part}'s {bytes}-byte snapshot was not written: {inner.Message}", inner)
{
    public int Bytes => bytes;
}

internal sealed class ChangeTooLargeException(string message) : InvalidOperationException(message);

// One part's log on the machine that holds its lease (Q18, Q26). Changes are group-committed as sealed mesh
// segments chained by Prev, and a change is durable only once a head the sequencer accepted names its segment.
// An own machine journals first and acknowledges from the journal, so it keeps writing through an outage.
internal sealed class PartLog : IAsyncDisposable
{
    private readonly PartId _part;
    public PartId Part => _part;
    // Replaced only by a re-seal (T26-5), under the flush lock and the queue lock.
    private byte[] _key;
    private readonly PartLease _lease;
    private readonly IMeshStore _mesh;
    private readonly Serializer _serializer;
    private readonly PartsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PartLog> _logger;
    private PartJournal? _journal;
    private readonly Dictionary<(string GrainId, string StateName), StoredRecord> _records = [];
    private readonly List<LogEntry> _since = [];
    // The segments this log wrote or replayed since the snapshot: what the next snapshot replaces and discards.
    private readonly List<MeshRef> _chain = [];
    // Only while opening: the accepted entries a journal is checked against.
    private readonly Dictionary<long, LogEntry> _replayed = [];
    private readonly List<Pending> _queue = [];
    // Own machine only, under the queue lock: the committed record of every key a queued change already applied
    // over (null: no record), so a snapshot folds the committed prefix while changes are still queued.
    private readonly Dictionary<(string GrainId, string StateName), StoredRecord?> _committedUnder = [];
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _drained = Completed();
    private Task _renewing = Task.CompletedTask;
    private TimeSpan _backoff = TimeSpan.FromSeconds(1);
    private TimeSpan _snapshotBackoff = TimeSpan.FromMinutes(1);
    private DateTimeOffset _snapshotNotBefore = DateTimeOffset.MinValue;
    private long _queuedBytes;
    private long _nextSeq;
    private long _applied;
    private long _signalsFloor;
    private Exception? _closed;
    private bool _disposed;

    private PartLog(PartId part, byte[] key, PartLease lease, IMeshStore mesh, Serializer serializer, PartsOptions options, TimeProvider time,
        ILogger<PartLog> logger, PartJournal? journal) =>
        (_part, _key, _lease, _mesh, _serializer, _options, _time, _logger, _journal) = (part, key, lease, mesh, serializer, options, time, logger, journal);

    private sealed record Pending(LogEntry Entry, TaskCompletionSource<long> Committed);

    public long LastSeq
    {
        get { lock (_records) { return _applied; } }
    }

    // After a lease loss or an ambiguous commit every Append fails; only a reopen replays the head again.
    public bool Closed
    {
        get { lock (_queue) { return _closed is not null || _disposed; } }
    }

    public bool LeaseLive => !Closed && !_lease.Expired;

    // The holder's own audit (T23-9) opens manifests with the part key; it never leaves this machine.
    internal byte[] Key => _key;

    internal HeadPayload Head => _lease.Payload;

    public static async Task<PartLog> Open(PartId part, byte[] key, PartLease lease, IMeshStore mesh, Serializer serializer, PartsOptions options,
        TimeProvider time, ILogger<PartLog> logger, CancellationToken ct, string? deploymentDirectory = null)
    {
        var journal = options.Role == MachineRole.Own
            ? new PartJournal(Path.Combine(deploymentDirectory ?? options.MachineDirectory, "journal", $"{HeadRecord.HashOf(part.Value)}.log"), key, serializer)
            : null;
        var log = new PartLog(part, key, lease, mesh, serializer, options, time, logger, journal);
        try
        {
            await log.Replay(ct);
            log.Recover();
        }
        // An own machine keeps its lease through a transient failure: its journal may hold changes no head covers.
        catch (Exception error) when (options.Role != MachineRole.Own || error is JournalConflictException)
        {
            await log.ReleaseQuietly();
            throw;
        }
        log._renewing = log.RenewLoop();
        return log;
    }

    // Every record this log holds, as of now.
    internal (string GrainId, string StateName, StoredRecord Record)[] Records()
    {
        lock (_records) { return [.. _records.Select(entry => (entry.Key.GrainId, entry.Key.StateName, entry.Value))]; }
    }

    // The grains this log holds a record for: what a release deactivates before another machine takes the part.
    internal string[] GrainIds
    {
        get { lock (_records) { return [.. _records.Keys.Select(key => key.GrainId).Distinct(StringComparer.Ordinal)]; } }
    }

    public StoredRecord? Read(string grainId, string stateName)
    {
        lock (_records) { return _records.GetValueOrDefault((grainId, stateName)); }
    }

    // Entries at or below this seq may hold signals this log no longer serves (they predate the snapshot before last).
    public long SignalsFloor
    {
        get { lock (_records) { return _signalsFloor; } }
    }

    public (long Floor, long Last, LogEntry[] Entries) SignalsOf(string grainId, long seq)
    {
        lock (_records) { return (_signalsFloor, _applied, [.. _since.Where(e => e.Seq > seq && e.GrainId == grainId && e.Signals.Length > 0)]); }
    }

    public IReadOnlyList<LogEntry> SignalsSince(long seq)
    {
        lock (_records) { return [.. _since.Where(e => e.Seq > seq && e.Signals.Length > 0)]; }
    }

    public Task<long> Append(string grainId, string stateName, RecordChange change, byte[] record, Signal[] signals) =>
        Append(grainId, stateName, change, record, signals, check: false, expected: null);

    // The storage ETag check, in the log's own order: a change applies only if the record is still at the
    // expected seq (null: no record), counting changes queued but not yet committed.
    public Task<long> AppendIfCurrent(string grainId, string stateName, long? expected, RecordChange change, byte[] record, Signal[] signals) =>
        Append(grainId, stateName, change, record, signals, check: true, expected);

    private Task<long> Append(string grainId, string stateName, RecordChange change, byte[] record, Signal[] signals, bool check, long? expected)
    {
        Pending pending;
        bool first, full;
        lock (_queue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closed is not null) { return Task.FromException<long>(_closed); }
            if (check && Current(grainId, stateName) is var current && current != expected)
            {
                return Task.FromException<long>(new Orleans.Storage.InconsistentStateException(
                    $"{grainId}/{stateName} is at {current?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no record"}, not {expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no record"}.",
                    current?.ToString(System.Globalization.CultureInfo.InvariantCulture), expected?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            var entry = new LogEntry { Seq = _nextSeq, GrainId = grainId, StateName = stateName, Change = change, Record = record, Signals = signals };
            Measure(entry);
            if (SizeOf(entry) > _options.MaxChangeBytes)
            {
                _logger.LogError("Part {Part} refused a {Bytes}-byte change to {GrainId}/{StateName}: no commit carries more than {Max} bytes.",
                    _part, SizeOf(entry), grainId, stateName, _options.MaxChangeBytes);
                return Task.FromException<long>(new ChangeTooLargeException(
                    $"A change of {SizeOf(entry)} bytes to {grainId}/{stateName} exceeds the {_options.MaxChangeBytes}-byte limit of part {_part}."));
            }
            if (_journal is not null)
            {
                try { _journal.Append(entry); }
                catch (Exception error) when (_journal.Broken)
                {
                    _logger.LogError(error, "Part {Part} could not repair its journal after a failed append; the log is closed.", _part);
                    _closed = error;
                    throw;
                }
                RememberCommitted(entry);
                Apply(entry);
            }
            _nextSeq++;
            pending = new Pending(entry, new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously));
            first = Enqueue(pending);
            full = _queuedBytes >= _options.BatchBytes;
        }
        if (full) { ScheduleFlush(TimeSpan.Zero); }
        else if (first) { ScheduleFlush(_options.BatchDelay); }
        return _options.AcknowledgeOnJournal && _journal is not null ? Task.FromResult(pending.Entry.Seq) : pending.Committed.Task;
    }

    // Caller holds the queue lock. The last queued change wins over the committed record.
    private long? Current(string grainId, string stateName)
    {
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            var entry = _queue[i].Entry;
            if (entry.GrainId == grainId && entry.StateName == stateName) { return entry.Change == RecordChange.Write ? entry.Seq : null; }
        }
        return Read(grainId, stateName)?.ETagSeq;
    }

    // Completes once every queued change is committed (or failed); asks for a flush now rather than at the next timer.
    internal Task Flushed()
    {
        Task drained;
        lock (_queue)
        {
            if (_queue.Count == 0) { return Task.CompletedTask; }
            drained = _drained.Task;
        }
        ScheduleFlush(TimeSpan.Zero);
        return drained;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _renewing;
        // One flush commits one bounded batch: keep flushing while each one makes progress.
        while (true)
        {
            int queued;
            lock (_queue) { queued = _closed is null ? _queue.Count : 0; }
            if (queued == 0) { break; }
            await Flush();
            lock (_queue) { if (_queue.Count >= queued) { break; } }
        }
        bool pending;
        lock (_queue)
        {
            _disposed = true;
            pending = _queue.Count > 0;
        }
        // An own machine with journaled changes the head has not accepted keeps its lease: letting another machine
        // take the part now would only turn those changes into a conflict.
        if (_closed is null && !pending) { await ReleaseQuietly(); }
    }

    private async Task Replay(CancellationToken ct)
    {
        var payload = _lease.Payload;
        if (payload.Snapshot is { } snapshotRef)
        {
            var snapshot = _serializer.Deserialize<PartSnapshot>(await _mesh.Read(_key, snapshotRef, ct))!;
            foreach (var record in snapshot.Records) { _records[(record.GrainId, record.StateName)] = new StoredRecord(record.Record, record.ETagSeq); }
            _since.AddRange(Readable(snapshot.RecentSignals));
            _signalsFloor = snapshot.SignalsFrom;
        }
        var segments = new List<(MeshRef Ref, LogSegment Segment)>();
        for (var reference = payload.Head; reference is not null;)
        {
            var segment = _serializer.Deserialize<LogSegment>(await _mesh.Read(_key, reference, ct))!;
            segments.Add((reference, segment));
            if (segment.FirstSeq <= payload.SnapshotSeq + 1) { break; }
            reference = segment.Prev;
        }
        segments.Reverse();
        foreach (var (reference, segment) in segments)
        {
            _chain.Add(reference);
            foreach (var entry in Readable(segment.Entries).Where(e => e.Seq > payload.SnapshotSeq && e.Seq <= payload.LastSeq))
            {
                Apply(entry);
                _replayed[entry.Seq] = entry;
            }
        }
        _applied = payload.LastSeq;
        _nextSeq = payload.LastSeq + 1;
    }

    // Journaled entries the head already covers must be the ones it accepted; the rest must continue right after
    // it. Anything else means another machine wrote the part meanwhile (Review Focus 2).
    private void Recover()
    {
        var replayed = _replayed.ToDictionary();
        _replayed.Clear();
        if (_journal is null) { return; }
        var lastSeq = _lease.Payload.LastSeq;
        LogEntry[] journaled;
        try { journaled = _journal.Read(); }
        // Sealed under another key (a store reset under a kept machine directory): it continues no head of this part.
        catch (System.Security.Cryptography.CryptographicException)
        {
            _journal.SetAside();
            throw new JournalConflictException($"The journal of part {_part} was sealed under another key; it is set aside.");
        }
        // A covered entry the replay cannot see (inside a snapshot) is this machine's own commit only if the head
        // durably records this machine as the writer of that seq (rulings R11, R27); who acquired last says nothing.
        var payload = _lease.Payload;
        var conflict = journaled.Where(e => e.Seq <= lastSeq)
            .Any(e => replayed.TryGetValue(e.Seq, out var accepted) ? !Same(accepted, e) : !payload.WrittenBy(_lease.MachineId, e.Seq));
        var rest = journaled.Where(e => e.Seq > lastSeq).ToArray();
        conflict |= rest.Select((e, i) => e.Seq != lastSeq + 1 + i).Any(gap => gap);
        if (conflict)
        {
            _journal.SetAside();
            throw new JournalConflictException($"The journal of part {_part} does not continue the head at seq {lastSeq}; another machine wrote the part meanwhile.");
        }
        _journal.TrimThrough(lastSeq);
        lock (_queue)
        {
            foreach (var entry in rest)
            {
                RememberCommitted(entry);
                Apply(entry);
                Measure(entry);
                Enqueue(new Pending(entry, new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously)));
            }
            _nextSeq = lastSeq + 1 + rest.Length;
        }
        if (rest.Length > 0) { ScheduleFlush(TimeSpan.Zero); }
    }

    private bool Same(LogEntry a, LogEntry b) =>
        a.GrainId == b.GrainId && a.StateName == b.StateName && a.Change == b.Change && a.Record.AsSpan().SequenceEqual(b.Record)
        && _serializer.SerializeToArray(a.Signals).AsSpan().SequenceEqual(_serializer.SerializeToArray(b.Signals));

    private void Apply(LogEntry entry)
    {
        lock (_records)
        {
            switch (entry.Change)
            {
                case RecordChange.Write: _records[(entry.GrainId, entry.StateName)] = new StoredRecord(entry.Record, entry.Seq); break;
                case RecordChange.Clear: _records.Remove((entry.GrainId, entry.StateName)); break;
            }
            if (entry.Signals.Length > 0) { _since.Add(entry); }
            _applied = Math.Max(_applied, entry.Seq);
        }
    }

    // Caller holds the queue lock.
    private void RememberCommitted(LogEntry entry)
    {
        var key = (entry.GrainId, entry.StateName);
        if (!_committedUnder.ContainsKey(key)) { _committedUnder[key] = Read(entry.GrainId, entry.StateName); }
    }

    // Caller holds the queue lock, after a batch committed: what it wrote is now the committed record, and a key
    // with no change left in the queue is committed as it stands.
    private void Committed(Pending[] batch)
    {
        foreach (var entry in batch.Select(p => p.Entry))
        {
            var key = (entry.GrainId, entry.StateName);
            if (!_committedUnder.ContainsKey(key)) { continue; }
            switch (entry.Change)
            {
                case RecordChange.Write: _committedUnder[key] = new StoredRecord(entry.Record, entry.Seq); break;
                case RecordChange.Clear: _committedUnder[key] = null; break;
            }
        }
        var queued = _queue.Select(p => (p.Entry.GrainId, p.Entry.StateName)).ToHashSet();
        foreach (var key in _committedUnder.Keys.Where(k => !queued.Contains(k)).ToArray()) { _committedUnder.Remove(key); }
    }

    // Caller holds the queue lock. True when this is the first change of a new batch.
    private bool Enqueue(Pending pending)
    {
        var first = _queue.Count == 0;
        if (first) { _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
        _queue.Add(pending);
        _queuedBytes += SizeOf(pending.Entry);
        return first;
    }

    // Sizes live beside the entries, never on LogEntry: a field there changes its codec and older segments stop reading.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LogEntry, System.Runtime.CompilerServices.StrongBox<long>> _sizes = new();

    private long SizeOf(LogEntry entry) => _sizes.TryGetValue(entry, out var size) ? size.Value : Measure(entry);

    // The record plus the serialized signals, with a fixed allowance for the entry's own fields.
    private long Measure(LogEntry entry)
    {
        var bytes = 64L + (entry.Record?.Length ?? 0) + (entry.Signals is not { Length: > 0 } signals ? 0 : _serializer.SerializeToArray(signals).Length);
        _sizes.AddOrUpdate(entry, new System.Runtime.CompilerServices.StrongBox<long>(bytes));
        return bytes;
    }

    // Entries read back from the mesh: a missing entry is skipped and a missing record or signal list reads as empty,
    // so a segment an earlier build wrote never fails the part's replay (and with it every activation in the part).
    private static IEnumerable<LogEntry> Readable(LogEntry?[]? entries)
    {
        foreach (var entry in entries ?? [])
        {
            if (entry is null) { continue; }
            entry.Signals ??= [];
            entry.Record ??= [];
            entry.GrainId ??= "";
            entry.StateName ??= "";
            yield return entry;
        }
    }

    // Caller holds the queue lock. At most BatchBytes of the queue (at least one change): a backlog an outage or a
    // slow commit built up commits in bounded segments, so it cannot outgrow what one commit carries and keep the
    // journal untrimmed forever.
    private Pending[] NextBatch()
    {
        var count = 0;
        long bytes = 0;
        while (count < _queue.Count && (count == 0 || bytes + SizeOf(_queue[count].Entry) <= _options.BatchBytes))
        {
            bytes += SizeOf(_queue[count].Entry);
            count++;
        }
        return [.. _queue.Take(count)];
    }

    private void ScheduleFlush(TimeSpan delay) => _ = FlushAfter(delay);

    private async Task FlushAfter(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) { await Task.Delay(delay, _time, _stop.Token); }
            else { await Task.Yield(); }
        }
        catch (OperationCanceledException) { return; }
        if (_stop.IsCancellationRequested) { return; }
        await Flush();
    }

    private async Task Flush()
    {
        await _flushing.WaitAsync();
        try
        {
            Pending[] batch;
            lock (_queue) { batch = NextBatch(); }
            if (batch.Length == 0) { return; }
            var payload = _lease.Payload;
            var segment = new LogSegment { FirstSeq = batch[0].Entry.Seq, Entries = [.. batch.Select(p => p.Entry)], Prev = payload.Head };
            try
            {
                var reference = await _mesh.Write(_key, PartSegments.SegmentId(_part, segment.FirstSeq), _serializer.SerializeToArray(segment));
                await _lease.Commit(new HeadPayload
                {
                    Head = reference, Snapshot = payload.Snapshot, SnapshotSeq = payload.SnapshotSeq,
                    LastSeq = batch[^1].Entry.Seq, SegmentsSinceSnapshot = payload.SegmentsSinceSnapshot + 1, Writers = payload.Writers,
                    AuditRound = payload.AuditRound,
                }.CommittedBy(_lease.MachineId, batch[0].Entry.Seq, batch[^1].Entry.Seq));
                _chain.Add(reference);
                if (_journal is null) { foreach (var pending in batch) { Apply(pending.Entry); } }
                TaskCompletionSource? drained;
                lock (_queue)
                {
                    _queue.RemoveRange(0, batch.Length);
                    _queuedBytes = _queue.Sum(p => SizeOf(p.Entry));
                    drained = _queue.Count == 0 ? _drained : null;
                    Committed(batch);
                }
                // Changes queued while this batch committed start the next batch; no later append schedules it.
                if (drained is null) { ScheduleFlush(_queuedBytes >= _options.BatchBytes ? TimeSpan.Zero : _options.BatchDelay); }
                // Q26: the first commit after failed attempts is what an operator watches for once the trust side is back.
                if (_backoff > TimeSpan.FromSeconds(1))
                { _logger.LogInformation("Part {Part} committed its journaled changes through seq {Seq} at head version {Version}.", _part, batch[^1].Entry.Seq, _lease.Version); }
                _backoff = TimeSpan.FromSeconds(1);
                // The batch is durable once the head accepted it; a stale journal is trimmed by the next flush or open.
                try { _journal?.TrimThrough(batch[^1].Entry.Seq); }
                catch (Exception error) { _logger.LogWarning(error, "Part {Part} could not trim its journal through seq {Seq}.", _part, batch[^1].Entry.Seq); }
                // The batch is acknowledged once its snapshot step is done; a failed snapshot commit still acknowledges
                // the durable batch and is then handled below exactly like a failed batch commit (ruling R12).
                try
                {
                    if (_lease.Payload.SegmentsSinceSnapshot >= _options.SnapshotEvery && _time.GetUtcNow() >= _snapshotNotBefore)
                    {
                        await Snapshot();
                        _snapshotBackoff = TimeSpan.FromMinutes(1);
                    }
                }
                // The snapshot never reached the mesh, so the head is exactly what the batch committed: the log keeps
                // committing batches at full speed and tries the snapshot again later. Treating it as a failed commit
                // put every batch behind the commit backoff, and the journal grew with the writes it could not flush.
                catch (SnapshotNotWrittenException notWritten)
                {
                    _logger.LogWarning(notWritten.InnerException, "Part {Part} could not write its {Bytes}-byte snapshot at seq {Seq} ({Reason}); the chain keeps growing and the snapshot is retried after {Delay}.",
                        _part, notWritten.Bytes, _lease.Payload.LastSeq, notWritten.InnerException?.Message, _snapshotBackoff);
                    _snapshotNotBefore = _time.GetUtcNow() + _snapshotBackoff;
                    _snapshotBackoff = TimeSpan.FromTicks(Math.Min(_snapshotBackoff.Ticks * 2, TimeSpan.FromMinutes(30).Ticks));
                }
                finally
                {
                    drained?.TrySetResult();
                    foreach (var pending in batch) { pending.Committed.TrySetResult(pending.Entry.Seq); }
                }
            }
            catch (LeaseLostException lost)
            {
                _logger.LogWarning(lost, "Part {Part} lost its lease; {Count} changes are not durable.", _part, batch.Length);
                lock (_queue) { _closed = lost; }
                FailAll(lost);
            }
            catch (Exception error) when (_journal is not null)
            {
                _logger.LogWarning(error, "Part {Part} could not commit; {Count} journaled changes wait for the next attempt.", _part, batch.Length);
                ScheduleFlush(_backoff);
                _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
            }
            // Ruling R12: an ambiguous failure closes the log like a lease loss. A write reported failed here may in
            // fact be durable (the head may have moved); reopening replays it from the head, which is the only truth.
            catch (Exception error)
            {
                _logger.LogWarning(error, "Part {Part} could not commit; {Count} changes fail and the log is closed.", _part, batch.Length);
                lock (_queue) { _closed = error; }
                FailAll(error);
            }
        }
        finally { _flushing.Release(); }
    }

    // Caller holds the flush lock. Folds the committed state into one snapshot that replaces the chain, then
    // discards what it replaced. Journaled entries at or below SnapshotSeq were trimmed by the flush that
    // committed them; queued ones stay above it. A reopen accepts covered entries the head records as its own (R27).
    private async Task Snapshot(bool resetAuditRound = false, byte[]? rekey = null)
    {
        var payload = _lease.Payload;
        SnapshotRecord[] records;
        LogEntry[] recent;
        lock (_queue)
        {
            // An own machine applies journaled changes before the head accepts them: fold the committed prefix only.
            lock (_records)
            {
                var committed = new Dictionary<(string GrainId, string StateName), StoredRecord>(_records);
                foreach (var (key, record) in _committedUnder)
                {
                    if (record is null) { committed.Remove(key); }
                    else { committed[key] = record; }
                }
                records = [.. committed.Select(r => new SnapshotRecord { GrainId = r.Key.GrainId, StateName = r.Key.StateName, Record = r.Value.Record, ETagSeq = r.Value.ETagSeq })];
                recent = [.. _since.Where(e => e.Seq > payload.SnapshotSeq && e.Seq <= payload.LastSeq)];
            }
        }
        var snapshot = new PartSnapshot { Seq = payload.LastSeq, Records = records, RecentSignals = recent, SignalsFrom = payload.SnapshotSeq };
        var replacedKey = _key;
        MeshRef reference;
        var plaintext = _serializer.SerializeToArray(snapshot);
        try { reference = await _mesh.Write(rekey ?? _key, PartSegments.SnapshotId(_part, payload.LastSeq), plaintext); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new SnapshotNotWrittenException(_part, plaintext.Length, error); }
        var head = new HeadPayload
        {
            Head = null, Snapshot = reference, SnapshotSeq = payload.LastSeq, LastSeq = payload.LastSeq, SegmentsSinceSnapshot = 0, Writers = payload.Writers,
            AuditRound = resetAuditRound ? 0 : payload.AuditRound,
        };
        if (rekey is null) { await _lease.Commit(head); }
        else
        {
            await _lease.CommitUnder(rekey, head);
            // Changes journaled meanwhile are sealed again with the rest; they commit under the new key.
            lock (_queue) { (_key, _journal) = (rekey, _journal?.Rekeyed(rekey)); }
        }
        // Only refs of this log's own replaced chain are discarded, never one a read repaired (ruling R7): MeshStore
        // shares shards between a repaired ref and its original. Open has no repair path today.
        // Serve exactly what a reopen from this snapshot would: signals after the previous snapshot's seq.
        lock (_records)
        {
            _since.RemoveAll(e => e.Seq <= payload.SnapshotSeq);
            _signalsFloor = Math.Max(_signalsFloor, payload.SnapshotSeq);
        }
        MeshRef[] replaced = [.. payload.Snapshot is { } previous ? [previous] : Array.Empty<MeshRef>(), .. _chain];
        _chain.Clear();
        foreach (var old in replaced)
        {
            try { await _mesh.Discard(replacedKey, old); }
            catch (Exception error) { _logger.LogWarning(error, "Part {Part} could not discard {Segment} after its snapshot at seq {Seq}.", _part, old.SegmentId, payload.LastSeq); }
        }
    }

    // T26-5: a rotated circle part key re-seals the part: its committed state as one snapshot and the head under the new
    // key, and the journal with them. Nothing written after this opens under the old key; what the old key sealed stays
    // readable to whoever holds it (R131). A failure after the snapshot was written leaves the head's key unknown, so
    // the log closes and the next open tries this machine's keys newest first.
    internal Task Reseal(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return UnderLease(async () =>
        {
            try { await Snapshot(rekey: key); }
            catch (Exception error) when (error is not LeaseLostException)
            {
                _logger.LogWarning(error, "Part {Part} could not re-seal under its rotated key; the log is closed.", _part);
                lock (_queue) { _closed = error; }
                FailAll(error);
                throw;
            }
            return true;
        });
    }

    // T23-9: what the holder audits: the head's snapshot and the chain segments written or replayed since it.
    internal async Task<(MeshRef? Snapshot, MeshRef[] Chain)> Reached()
    {
        await _flushing.WaitAsync();
        try { return (_lease.Payload.Snapshot, [.. _chain]); }
        finally { _flushing.Release(); }
    }

    // Runs `act` only while the head still reaches `reference` (its snapshot, or a chain segment of it), holding off any
    // snapshot that would discard it meanwhile: the holder reports an audit only for refs it still keeps (R7, T23-9).
    internal async Task<(bool Reached, T? Result)> WhileReached<T>(MeshRef reference, Func<Task<T>> act)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await _flushing.WaitAsync();
        try
        {
            var reached = _lease.Payload.Snapshot?.ManifestCid == reference.ManifestCid || _chain.Any(chained => chained.ManifestCid == reference.ManifestCid);
            return reached ? (true, await act()) : (false, default);
        }
        finally { _flushing.Release(); }
    }

    // R111: one CAS per audit pass of the part; the next holder continues from the committed round.
    internal Task<int> AdvanceAuditRound() => UnderLease(async () =>
    {
        var payload = _lease.Payload;
        await _lease.Commit(Copy(payload, payload.Snapshot, payload.AuditRound + 1));
        return payload.AuditRound + 1;
    });

    // T23-9: a failed chain segment cannot be repaired in place (segments link by Prev inside sealed bytes), so an
    // early snapshot replaces the chain and discards it. R110/R116: on challenge exhaustion the snapshot is the
    // re-issue: every ref the head reaches is then new, so the round restarts at 0.
    internal Task HealBySnapshot(bool resetAuditRound = false) => UnderLease(async () =>
    {
        await Snapshot(resetAuditRound);
        return true;
    });

    // T23-9/R7: a repaired snapshot ref replaces the head's one in a CAS under the lease; the old ref is not discarded
    // (the repaired manifest shares its unmoved shards). False when the head no longer names `expected`.
    internal Task<bool> CommitRepairedSnapshot(MeshRef expected, MeshRef repaired) => UnderLease(async () =>
    {
        var payload = _lease.Payload;
        if (payload.Snapshot?.ManifestCid != expected.ManifestCid) { return false; }
        await _lease.Commit(Copy(payload, repaired, payload.AuditRound));
        return true;
    });

    private static HeadPayload Copy(HeadPayload payload, MeshRef? snapshot, int auditRound) => new()
    {
        Head = payload.Head, Snapshot = snapshot, SnapshotSeq = payload.SnapshotSeq, LastSeq = payload.LastSeq,
        SegmentsSinceSnapshot = payload.SegmentsSinceSnapshot, Writers = payload.Writers, AuditRound = auditRound,
    };

    // Serialized with flushes and snapshots; a lost lease closes the log exactly as a failed batch commit does.
    private async Task<T> UnderLease<T>(Func<Task<T>> change)
    {
        await _flushing.WaitAsync();
        try
        {
            lock (_queue)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_closed is not null) { throw new LeaseLostException($"The log of part {_part} is closed: {_closed.Message}"); }
            }
            return await change();
        }
        catch (LeaseLostException lost) when (!Closed)
        {
            lock (_queue) { _closed = lost; }
            FailAll(lost);
            throw;
        }
        finally { _flushing.Release(); }
    }

    // Seqs restart right after what the head accepted, so the next batch continues it without a gap.
    private void FailAll(Exception error)
    {
        Pending[] failed;
        lock (_queue)
        {
            failed = [.. _queue];
            _queue.Clear();
            _committedUnder.Clear();
            _queuedBytes = 0;
            if (_journal is null) { _nextSeq = _lease.Payload.LastSeq + 1; }
            _drained.TrySetResult();
        }
        foreach (var pending in failed) { pending.Committed.TrySetException(error); }
    }

    private async Task RenewLoop()
    {
        var period = _options.Role == MachineRole.Own ? _options.OwnLease / 8 : _options.RenewBefore / 2;
        using var timer = new PeriodicTimer(period, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                bool busy;
                lock (_queue) { busy = _queue.Count > 0 || _closed is not null; }
                // A pending flush renews through its own commit; an expired lease is settled by the next CAS, not revived.
                if (busy || _lease.Expired || !_lease.NeedsRenewal) { continue; }
                try { await _lease.Renew(); }
                catch (LeaseLostException lost)
                {
                    lock (_queue) { _closed = lost; }
                    FailAll(lost);
                }
                catch (Exception error) { _logger.LogWarning(error, "Part {Part} could not renew its lease.", _part); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReleaseQuietly()
    {
        try { await _lease.Release(); }
        catch (Exception error) { _logger.LogWarning(error, "Part {Part} could not release its lease.", _part); }
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }
}
