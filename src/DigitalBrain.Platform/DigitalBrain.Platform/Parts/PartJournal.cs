using System.Buffers.Binary;
using DigitalBrain.Platform.Mesh;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// An own machine's local write-ahead journal for one part (Q26): each entry is sealed and on disk before it is
// acknowledged, and dropped once a head the sequencer accepted covers it.
internal sealed class PartJournal(string path, byte[] key, Serializer serializer)
{
    private readonly Lock _gate = new();

    // Where each entry starts in the file, in seq order, once the file was read or written by this instance: a trim
    // then copies the kept tail as raw bytes instead of opening every entry again.
    private List<(long Seq, long Offset)>? _index;
    private long _end;

    // Test seam: runs between a record's length and its body, so a failure leaves a half-written record.
    internal Action? Interrupt { get; set; }

    // The most bytes one read call asks for: a journal is read record by record and a record in chunks of this size,
    // so the file's total size never meets a single-read limit (File.ReadAllBytes stops at 2 GB).
    internal int SingleReadLimit { get; init; } = 1 << 20;

    // A failed append could not be cut back off the file: nothing appended after it would read back.
    public bool Broken { get; private set; }

    private string Context => $"part-journal|{path}";

    public void Append(LogEntry entry)
    {
        lock (_gate)
        {
            EnsureDirectory();
            if (Stale()) { Scan(); }
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var start = file.Length;
            try
            {
                WriteRecord(file, entry);
                file.Flush(flushToDisk: true);
            }
            catch
            {
                // A half-written record would hide every later one from Read; cut it off or refuse further appends.
                try
                {
                    file.SetLength(start);
                    file.Flush(flushToDisk: true);
                }
                catch { Broken = true; }
                throw;
            }
            (_index ??= []).Add((entry.Seq, start));
            _end = file.Length;
        }
    }

    public LogEntry[] Read()
    {
        lock (_gate) { return Scan(); }
    }

    // Drops every entry at or below seq from the file itself: the kept tail is copied as raw bytes, and a journal the
    // head fully covers is truncated to nothing.
    public void TrimThrough(long seq)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) { return; }
            if (Stale()) { Scan(); }
            var index = _index!;
            var first = index.FindIndex(e => e.Seq > seq);
            if (first == 0) { return; }
            if (first < 0)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                file.SetLength(0);
                file.Flush(flushToDisk: true);
                index.Clear();
                _end = 0;
                return;
            }
            var from = index[first].Offset;
            var temp = $"{path}.tmp";
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.Position = from;
                var buffer = new byte[SingleReadLimit];
                for (var left = _end - from; left > 0;)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read == 0) { throw new EndOfStreamException($"The journal {path} ended before its indexed end."); }
                    target.Write(buffer, 0, read);
                    left -= read;
                }
                target.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            _index = [.. index.Skip(first).Select(e => (e.Seq, e.Offset - from))];
            _end -= from;
        }
    }

    // T26-5: the same file, every entry it holds sealed again under a rotated key; this instance is not used after it.
    public PartJournal Rekeyed(byte[] rotated)
    {
        lock (_gate)
        {
            var next = new PartJournal(path, rotated, serializer);
            if (!File.Exists(path)) { return next; }
            var entries = Scan();
            var temp = $"{path}.rekey";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var entry in entries) { next.WriteRecord(file, entry); }
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            _index = null;
            return next;
        }
    }

    public void SetAside()
    {
        lock (_gate)
        {
            if (File.Exists(path)) { File.Move(path, $"{path}.conflict-{DateTime.UtcNow:yyyyMMddHHmmss}"); }
            _index = null;
            _end = 0;
        }
    }

    // Caller holds the gate. The index is this instance's view: a file that another writer (another instance on the
    // same path) changed since then is scanned again, so a trim never copies only up to an old end.
    private bool Stale() => _index is null || (File.Exists(path) ? new FileInfo(path).Length : 0) != _end;

    private void EnsureDirectory() => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

    private void WriteRecord(FileStream file, LogEntry entry)
    {
        var sealedBytes = SegmentSeal.Close(key, Context, serializer.SerializeToArray(entry));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, sealedBytes.Length);
        file.Write(length);
        Interrupt?.Invoke();
        file.Write(sealedBytes);
    }

    // Caller holds the gate. Reads entry by entry and rebuilds the index. A record cut short by a crash mid-append is
    // the torn tail: everything before it was flushed whole, and the tail is cut off so later appends read back.
    private LogEntry[] Scan()
    {
        var entries = new List<LogEntry>();
        var index = new List<(long Seq, long Offset)>();
        long offset = 0;
        if (File.Exists(path))
        {
            long fileLength;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1))
            {
                fileLength = file.Length;
                var header = new byte[4];
                while (offset + 4 <= fileLength)
                {
                    file.Position = offset;
                    if (!Fill(file, header, 4)) { break; }
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length <= 0 || offset + 4 + length > fileLength) { break; }
                    var body = new byte[length];
                    if (!Fill(file, body, length)) { break; }
                    var entry = serializer.Deserialize<LogEntry>(SegmentSeal.Open(key, Context, body))!;
                    entries.Add(entry);
                    index.Add((entry.Seq, offset));
                    offset += 4 + length;
                }
            }
            if (offset < fileLength)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                file.SetLength(offset);
                file.Flush(flushToDisk: true);
            }
        }
        _index = index;
        _end = offset;
        return [.. entries];
    }

    private bool Fill(FileStream file, byte[] buffer, int count)
    {
        for (var at = 0; at < count;)
        {
            var read = file.Read(buffer, at, Math.Min(SingleReadLimit, count - at));
            if (read == 0) { return false; }
            at += read;
        }
        return true;
    }
}
