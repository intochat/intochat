using System.Buffers.Binary;
using DigitalBrain.Framework;
using DigitalBrain.Platform.Storage;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Platform.Storage;

// One local writer. State and its signals share a flushed, authenticated journal entry.
internal sealed class BrainLog : IDisposable
{
    private const int MaxEntryBytes = 16 * 1024 * 1024;
    private readonly string directory;
    private readonly string brainId;
    private readonly byte[] key;
    private readonly Serializer serializer;
    private readonly FileStream ownership;
    private readonly Lock gate = new();
    private Dictionary<string, Dictionary<string, BrainRecord>> records = new(StringComparer.Ordinal);
    private readonly List<BrainEntry> signals = [];
    private string generation = "initial";
    private long sequence;
    private long lastSignal;
    private Exception? failure;
    private bool disposed;

    public BrainLog(string directory, string brainId, byte[] key, Serializer serializer)
    {
        this.directory = directory;
        this.brainId = brainId;
        this.key = key;
        this.serializer = serializer;
        Directory.CreateDirectory(directory);
        ownership = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { Recover(); }
        catch { ownership.Dispose(); throw; }
    }

    public long LastSignal { get { lock (gate) { return lastSignal; } } }

    public BrainRecord? Read(string grainId, string stateName)
    {
        lock (gate)
        {
            RequireOpen();
            return records.TryGetValue(grainId, out var states) && states.TryGetValue(stateName, out var record)
                ? new BrainRecord((byte[])record.Record.Clone(), record.ETag) : null;
        }
    }

    public Task<long> Append(string grainId, string stateName, long? expectedETag, BrainRecordChange change, byte[] record, Signal[] emitted, CancellationToken ct)
    {
        lock (gate)
        {
            RequireOpen();
            ct.ThrowIfCancellationRequested();
            var current = records.GetValueOrDefault(grainId)?.GetValueOrDefault(stateName);
            if (change != BrainRecordChange.None && current?.ETag != expectedETag)
            { throw new InconsistentStateException($"State {grainId}/{stateName} changed since it was read."); }
            var entry = new BrainEntry(checked(sequence + 1), grainId, stateName, change, record, emitted, checked(lastSignal + 1));
            var plain = serializer.SerializeToArray(entry);
            if (plain.Length > MaxEntryBytes) { throw new ArgumentException("A brain change exceeds 16 MiB.", nameof(record)); }
            var bytes = SegmentSeal.Close(key, Context("log"), plain);
            try
            {
                using var stream = new FileStream(Journal, FileMode.Append, FileAccess.Write, FileShare.Read);
                Span<byte> length = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                stream.Write(length);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                // Deserialize the durable bytes so callers cannot mutate the stored projection.
                Apply(serializer.Deserialize<BrainEntry>(plain)!);
            }
            catch (Exception error) { failure = error; throw; }
            return Task.FromResult(sequence);
        }
    }

    public Task<JournaledSignal[]> ReadSignals(long since, int limit, string? publisher, CancellationToken ct)
    {
        lock (gate)
        {
            RequireOpen();
            ct.ThrowIfCancellationRequested();
            if (since < 0 || limit < 1) { throw new ArgumentOutOfRangeException(nameof(since)); }
            JournaledSignal[] result = [.. signals.Where(entry => publisher is null || entry.GrainId == publisher)
                .SelectMany(entry => entry.Signals.Select((signal, index) => new JournaledSignal(entry.FirstSignal + index, signal)))
                .Where(signal => signal.Sequence > since).Take(limit)];
            return Task.FromResult(result.Select(item => new JournaledSignal(item.Sequence, serializer.Deserialize<Signal>(serializer.SerializeToArray(item.Signal))!)).ToArray());
        }
    }

    public Task<PartSignals> PublisherSignals(string publisher, long since, CancellationToken ct)
    {
        lock (gate)
        {
            var read = ReadSignals(since, int.MaxValue, publisher, ct).GetAwaiter().GetResult();
            return Task.FromResult(new PartSignals(0, lastSignal, read));
        }
    }

    public Task Checkpoint(CancellationToken ct)
    {
        lock (gate)
        {
            RequireOpen();
            ct.ThrowIfCancellationRequested();
            var previous = generation;
            var next = Guid.NewGuid().ToString("N");
            var snapshot = new BrainSnapshot(sequence, lastSignal, records, [.. signals]);
            WriteFlushed(Path.Combine(directory, next + ".snapshot"), SegmentSeal.Close(key, Context("snapshot", next), serializer.SerializeToArray(snapshot)));
            WriteFlushed(Path.Combine(directory, next + ".log"), []);
            WriteFlushed(Path.Combine(directory, "current.tmp"), System.Text.Encoding.UTF8.GetBytes(next));
            try { File.Move(Path.Combine(directory, "current.tmp"), Path.Combine(directory, "current"), overwrite: true); }
            catch (Exception error) { failure = error; throw; }
            generation = next;
            // Old complete generation remains until the manifest points to the new one.
            foreach (var suffix in new[] { ".snapshot", ".log" })
            {
                try { File.Delete(Path.Combine(directory, previous + suffix)); }
                catch (IOException) { /* Unreachable generations may be removed on a later maintenance run. */ }
            }
            return Task.CompletedTask;
        }
    }

    private string Journal => Path.Combine(directory, generation + ".log");
    private string Context(string kind, string? version = null) => $"brain-store|{brainId}|{version ?? generation}|{kind}";

    private void Recover()
    {
        var manifest = Path.Combine(directory, "current");
        if (File.Exists(manifest))
        {
            generation = File.ReadAllText(manifest);
            if (!Guid.TryParseExact(generation, "N", out _)) { throw new InvalidDataException("Invalid brain checkpoint generation."); }
            var checkpoint = serializer.Deserialize<BrainSnapshot>(SegmentSeal.Open(key, Context("snapshot"), File.ReadAllBytes(Path.Combine(directory, generation + ".snapshot"))))!;
            records = checkpoint.Records;
            signals.AddRange(checkpoint.Signals.Select(SignalOnly));
            sequence = checkpoint.Sequence;
            lastSignal = checkpoint.LastSignal;
            if (!File.Exists(Journal)) { throw new InvalidDataException("The checkpoint journal is missing."); }
        }
        else if (!File.Exists(Journal) && (Directory.EnumerateFiles(directory, "*.snapshot").Any()
            || Directory.EnumerateFiles(directory, "*.log").Any()))
        { throw new InvalidDataException("The brain checkpoint manifest is missing; refusing to reset existing data."); }
        using var stream = new FileStream(Journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        long valid = 0;
        Span<byte> header = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 4) { break; }
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 28 || length > MaxEntryBytes + 28) { throw new InvalidDataException("Invalid encrypted entry length."); }
            if (stream.Length - stream.Position < length) { break; }
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            var entry = serializer.Deserialize<BrainEntry>(SegmentSeal.Open(key, Context("log"), bytes))!;
            if (entry.Sequence != sequence + 1 || entry.FirstSignal != lastSignal + 1)
            { throw new InvalidDataException("The local brain log is not contiguous."); }
            Apply(entry);
            valid = stream.Position;
        }
        if (valid != stream.Length) { stream.SetLength(valid); stream.Flush(flushToDisk: true); }
    }

    private void Apply(BrainEntry entry)
    {
        if (entry.Change == BrainRecordChange.Write)
        {
            if (!records.TryGetValue(entry.GrainId, out var states)) { records[entry.GrainId] = states = new(StringComparer.Ordinal); }
            states[entry.StateName] = new BrainRecord(entry.Record, entry.Sequence);
        }
        else if (entry.Change == BrainRecordChange.Clear) { records.GetValueOrDefault(entry.GrainId)?.Remove(entry.StateName); }
        if (entry.Signals.Length > 0) { signals.Add(SignalOnly(entry)); }
        sequence = entry.Sequence;
        lastSignal = entry.FirstSignal + entry.Signals.Length - 1;
    }

    private static BrainEntry SignalOnly(BrainEntry entry)
        => entry with { Record = [], StateName = "", Change = BrainRecordChange.None };

    internal static void WriteFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure is not null) { throw new IOException("The brain store must be reopened after a failed append or checkpoint.", failure); }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) { return; } disposed = true; ownership.Dispose(); }
    }
}

