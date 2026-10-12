using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Parts;

// None is an entry that only publishes signals.
[GenerateSerializer, Alias("platform.parts.record-change")]
public enum RecordChange
{
    None = 0,
    Write = 1,
    Clear = 2,
}

// One entry is one neuron change: its state record and the signals it published, durable together.
[GenerateSerializer, Alias("platform.parts.entry")]
public sealed class LogEntry
{
    [Id(0)] public long Seq { get; set; }

    [Id(1)] public string GrainId { get; set; } = "";

    [Id(2)] public string StateName { get; set; } = "";

    [Id(3)] public RecordChange Change { get; set; }

    [Id(4)] public byte[] Record { get; set; } = [];

    [Id(5)] public Signal[] Signals { get; set; } = [];
}

[GenerateSerializer, Alias("platform.parts.segment")]
public sealed class LogSegment
{
    [Id(0)] public long FirstSeq { get; set; }

    [Id(1)] public LogEntry[] Entries { get; set; } = [];

    [Id(2)] public MeshRef? Prev { get; set; }
}

// Ledger 38: a part snapshot is one object the head names (PartSegments.SnapshotId). It carries its records and
// recent signals inline while it fits SnapshotChunkBytes; larger snapshots keep only the chunk refs, because one
// sealed mesh segment is capped at DataShards x MaxShardBytes (128 MiB by default). RecordChunks/SignalChunks are
// empty exactly when the inline arrays are authoritative.
[GenerateSerializer, Alias("platform.parts.snapshot-index")]
public sealed class PartSnapshotIndex
{
    [Id(0)] public long Seq { get; set; }

    // The signal entries hold every signal after this seq; older ones went with the snapshot before.
    [Id(1)] public long SignalsFrom { get; set; }

    [Id(2)] public SnapshotRecord[] Records { get; set; } = [];

    [Id(3)] public LogEntry[] RecentSignals { get; set; } = [];

    [Id(4)] public MeshRef[] RecordChunks { get; set; } = [];

    [Id(5)] public MeshRef[] SignalChunks { get; set; } = [];
}

[GenerateSerializer, Alias("platform.parts.snapshot-records")]
public sealed class PartRecordChunk
{
    [Id(0)] public SnapshotRecord[] Records { get; set; } = [];
}

[GenerateSerializer, Alias("platform.parts.snapshot-signals")]
public sealed class PartSignalChunk
{
    [Id(0)] public LogEntry[] Entries { get; set; } = [];
}

[GenerateSerializer, Alias("platform.parts.snapshot-record")]
public sealed class SnapshotRecord
{
    [Id(0)] public string GrainId { get; set; } = "";

    [Id(1)] public string StateName { get; set; } = "";

    [Id(2)] public byte[] Record { get; set; } = [];

    [Id(3)] public long ETagSeq { get; set; }
}

[GenerateSerializer, Alias("platform.parts.head")]
public sealed class HeadPayload
{
    [Id(0)] public MeshRef? Head { get; set; }

    [Id(1)] public MeshRef? Snapshot { get; set; }

    [Id(2)] public long SnapshotSeq { get; set; }

    [Id(3)] public long LastSeq { get; set; }

    [Id(4)] public int SegmentsSinceSnapshot { get; set; }

    // Which machine committed which seqs, kept across snapshots: an own machine's covered journal entry is its own
    // history only if a range of its own covers that seq (ruling R27). A range is added only when the holder changes.
    [Id(5)] public SeqWriter[] Writers { get; set; } = [];

    // R111/R116: the next audit round for every ref this head reaches, committed with the lease so a new holder continues
    // it. Every rewrite carries it forward; only an exhaustion snapshot (all refs new) resets it to 0.
    [Id(6)] public int AuditRound { get; set; }

    public HeadPayload CommittedBy(string machineId, long firstSeq, long lastSeq)
    {
        var writers = Writers;
        if (lastSeq >= firstSeq)
        {
            writers = writers is [.., var last] && last.MachineId == machineId && last.ThroughSeq + 1 == firstSeq
                ? [.. writers[..^1], new SeqWriter { MachineId = machineId, FromSeq = last.FromSeq, ThroughSeq = lastSeq }]
                : [.. writers, new SeqWriter { MachineId = machineId, FromSeq = firstSeq, ThroughSeq = lastSeq }];
        }
        return new HeadPayload
        {
            Head = Head, Snapshot = Snapshot, SnapshotSeq = SnapshotSeq, LastSeq = LastSeq, SegmentsSinceSnapshot = SegmentsSinceSnapshot, Writers = writers,
            AuditRound = AuditRound,
        };
    }

    public bool WrittenBy(string machineId, long seq) => Writers.Any(w => w.MachineId == machineId && w.FromSeq <= seq && seq <= w.ThroughSeq);
}

[GenerateSerializer, Alias("platform.parts.seq-writer")]
public sealed class SeqWriter
{
    [Id(0)] public string MachineId { get; set; } = "";

    [Id(1)] public long FromSeq { get; set; }

    [Id(2)] public long ThroughSeq { get; set; }
}

public static class PartSegments
{
    public static string SegmentId(PartId part, long firstSeq) => $"{part.Value}/log/{firstSeq}";

    public static string SnapshotId(PartId part, long seq) => $"{part.Value}/snapshot/{seq}";

    public static string RecordChunkId(PartId part, long seq, int index) => $"{part.Value}/snapshot/{seq}/records/{index}";

    public static string SignalChunkId(PartId part, long seq, int index) => $"{part.Value}/snapshot/{seq}/signals/{index}";
}
