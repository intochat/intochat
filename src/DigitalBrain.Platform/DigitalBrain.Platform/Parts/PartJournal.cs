using System.Buffers.Binary;
using DigitalBrain.Platform.Mesh;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// An own machine's local write-ahead journal for one part (Q26): each entry is sealed and on disk before it is
// acknowledged, and dropped once a head the sequencer accepted covers it.
internal sealed class PartJournal(string path, byte[] key, Serializer serializer)
{
    private readonly Lock _gate = new();

    // Test seam: runs between a record's length and its body, so a failure leaves a half-written record.
    internal Action? Interrupt { get; set; }

    // A failed append could not be cut back off the file: nothing appended after it would read back.
    public bool Broken { get; private set; }

    private string Context => $"part-journal|{path}";

    public void Append(LogEntry entry)
    {
        lock (_gate)
        {
            EnsureDirectory();
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
        }
    }

    public LogEntry[] Read()
    {
        lock (_gate) { return ReadAll(); }
    }

    public void TrimThrough(long seq)
    {
        lock (_gate)
        {
            var keep = ReadAll().Where(e => e.Seq > seq).ToArray();
            EnsureDirectory();
            var temp = $"{path}.tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var entry in keep) { WriteRecord(file, entry); }
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
    }

    public void SetAside()
    {
        lock (_gate)
        {
            if (File.Exists(path)) { File.Move(path, $"{path}.conflict-{DateTime.UtcNow:yyyyMMddHHmmss}"); }
        }
    }

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

    // A record cut short by a crash mid-append is the torn tail: everything before it was flushed whole.
    private LogEntry[] ReadAll()
    {
        if (!File.Exists(path)) { return []; }
        var bytes = File.ReadAllBytes(path);
        var entries = new List<LogEntry>();
        var offset = 0;
        while (offset + 4 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            if (length <= 0 || offset + 4 + length > bytes.Length) { break; }
            var plain = SegmentSeal.Open(key, Context, bytes.AsSpan(offset + 4, length).ToArray());
            entries.Add(serializer.Deserialize<LogEntry>(plain)!);
            offset += 4 + length;
        }
        return [.. entries];
    }
}
