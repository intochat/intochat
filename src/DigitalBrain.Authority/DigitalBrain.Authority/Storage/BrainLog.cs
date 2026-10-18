using System.Buffers.Binary;
using DigitalBrain.Framework;
using DigitalBrain.Authority.Storage;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Authority.Storage;

// One local writer. State and its signals share a flushed, authenticated journal entry.
internal sealed class BrainLog : IDisposable
{
    private const int MaxEntryBytes = 16 * 1024 * 1024;
    private const int SegmentHeaderBytes = 5;
    private static ReadOnlySpan<byte> SegmentMagic => "DBLG"u8;
    private readonly string directory;
    private readonly string brainId;
    private readonly byte[] key;
    private readonly Serializer serializer;
    private readonly FileStream ownership;
    private readonly IBrainLogFiles files;
    private readonly Lock gate = new();
    private readonly BrainLogFormat writeFormat;
    private Dictionary<string, Dictionary<string, BrainRecord>> records = new(StringComparer.Ordinal);
    private readonly List<BrainEntry> signals = [];
    private string generation = "initial";
    private long sequence;
    private long lastSignal;
    private BrainLogFormat format;
    private Exception? failure;
    private bool disposed;

    public BrainLog(string directory, string brainId, byte[] key, Serializer serializer, BrainLogFormat writeFormat = BrainLogFormat.Legacy,
        IBrainLogFiles? files = null)
        : this(directory, brainId, key, serializer, writeFormat, Claim(directory, brainId), files) { }

    // Takes ownership of an already claimed writer lock (see Claim).
    internal BrainLog(string directory, string brainId, byte[] key, Serializer serializer, BrainLogFormat writeFormat, FileStream ownership,
        IBrainLogFiles? files = null)
    {
        this.ownership = ownership;
        this.files = files ?? FileBrainLogFiles.Instance;
        this.writeFormat = writeFormat;
        this.directory = directory;
        this.brainId = brainId;
        this.key = key;
        this.serializer = serializer;
        try { Recover(); }
        catch { ownership.Dispose(); throw; }
    }

    // The per-brain writer lock is the ownership token: a second writer, in this or another process, is refused.
    internal static FileStream Claim(string directory, string brainId)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when (error is not FileNotFoundException and not DirectoryNotFoundException)
        { throw new BrainNotOwnedException(brainId, error); }
    }

    public long LastSignal { get { lock (gate) { return lastSignal; } } }

    // Format of the active segment: detected from its header, or the write format for an empty segment.
    public BrainLogFormat Format { get { lock (gate) { return format; } } }

    internal BrainExport Export(Func<string, string, byte[], object?> decode)
    {
        lock (gate)
        {
            RequireOpen();
            var exported = new List<BrainExportRecord>();
            var omitted = 0;
            foreach (var grain in records)
            {
                foreach (var state in grain.Value)
                {
                    if (state.Key is "vault" or "credential" or "registration" or "access" or "account" or "principal" or "reminders")
                    { omitted++; continue; }
                    var value = decode(grain.Key, state.Key, state.Value.Record);
                    if (value is null || value.GetType().Assembly == typeof(BrainLog).Assembly || value is DigitalBrain.Framework.Platform.Connections.ConnectionState)
                    { omitted++; continue; }
                    exported.Add(new(grain.Key, state.Key, value.GetType().FullName!, System.Text.Json.JsonSerializer.SerializeToElement(value, value.GetType())));
                }
            }
            return new(1, brainId, sequence, exported.ToArray(), omitted);
        }
    }

    internal bool CheckStorage(CancellationToken ct)
    {
        lock (gate)
        {
            RequireOpen();
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, ".readiness-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    4096, FileOptions.DeleteOnClose | FileOptions.WriteThrough);
                probe.WriteByte(0x5A);
                probe.Flush(flushToDisk: true);
                probe.Position = 0;
                return probe.ReadByte() == 0x5A;
            }
            finally { File.Delete(path); }
        }
    }

    public BrainRecord? Read(string grainId, string stateName)
    {
        lock (gate)
        {
            RequireOpen();
            return records.TryGetValue(grainId, out var states) && states.TryGetValue(stateName, out var record)
                ? new BrainRecord((byte[])record.Record.Clone(), record.ETag) : null;
        }
    }

    public async Task<long> Append(string grainId, string stateName, long? expectedETag, BrainRecordChange change, byte[] record, Signal[] emitted, CancellationToken ct)
        => (await AppendEntry(grainId, stateName, expectedETag, change, record, emitted, ct)).Sequence;

    // The entry's sequence (a record's ETag) and the sequence of its last signal, which Read and nudges report.
    public Task<(long Sequence, long LastSignal)> AppendEntry(string grainId, string stateName, long? expectedETag, BrainRecordChange change, byte[] record, Signal[] emitted, CancellationToken ct)
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
                using var stream = files.OpenAppend(Journal);
                // Header and first frame go out in one write; a torn header is truncated on recovery.
                var headed = stream.Length == 0 && format == BrainLogFormat.Framed;
                var frame = new byte[(headed ? SegmentHeaderBytes : 0) + 4 + bytes.Length];
                var offset = 0;
                if (headed) { SegmentMagic.CopyTo(frame); frame[4] = (byte)BrainLogFormat.Framed; offset = SegmentHeaderBytes; }
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset), bytes.Length);
                bytes.CopyTo(frame.AsSpan(offset + 4));
                stream.Write(frame);
                FlushToDisk(stream);
                // Deserialize the durable bytes so callers cannot mutate the stored projection.
                Apply(serializer.Deserialize<BrainEntry>(plain)!);
            }
            catch (Exception error) { failure = error; throw; }
            return Task.FromResult((sequence, lastSignal));
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
            var snapshot = new BrainSnapshot(sequence, lastSignal, records, [.. signals], writeFormat);
            WriteFlushed(Path.Combine(directory, next + ".snapshot"), SegmentSeal.Close(key, Context("snapshot", next), serializer.SerializeToArray(snapshot)));
            WriteFlushed(Path.Combine(directory, next + ".log"), []);
            WriteFlushed(Path.Combine(directory, "current.tmp"), System.Text.Encoding.UTF8.GetBytes(next));
            try { File.Move(Path.Combine(directory, "current.tmp"), Path.Combine(directory, "current"), overwrite: true); }
            catch (Exception error) { failure = error; throw; }
            generation = next;
            format = writeFormat;
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
        using var stream = files.OpenRead(Journal);
        long valid = DetectFormat(stream);
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
        if (valid != stream.Length) { stream.SetLength(valid); FlushToDisk(stream); }
    }

    // Legacy segments start with a length below 16 MiB, so they never begin with "DBLG".
    private long DetectFormat(Stream stream)
    {
        Span<byte> header = stackalloc byte[SegmentHeaderBytes];
        var read = stream.ReadAtLeast(header, SegmentHeaderBytes, throwOnEndOfStream: false);
        stream.Position = 0;
        if (read == 0) { format = writeFormat; return 0; }
        var magic = Math.Min(read, SegmentMagic.Length);
        if (!header[..magic].SequenceEqual(SegmentMagic[..magic])) { format = BrainLogFormat.Legacy; return 0; }
        if (read < SegmentHeaderBytes)
        {
            // A torn header holds no entries.
            stream.SetLength(0);
            FlushToDisk(stream);
            format = writeFormat;
            return 0;
        }
        if (header[4] != (byte)BrainLogFormat.Framed)
        { throw new InvalidDataException($"Unknown brain log format version {header[4]}."); }
        format = BrainLogFormat.Framed;
        stream.Position = SegmentHeaderBytes;
        return SegmentHeaderBytes;
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

    private static void FlushToDisk(Stream stream)
    {
        if (stream is FileStream file) { file.Flush(flushToDisk: true); }
        else { stream.Flush(); }
    }

    internal static void WriteFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        FlushToDisk(stream);
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

