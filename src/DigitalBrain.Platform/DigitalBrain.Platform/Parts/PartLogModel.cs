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

[GenerateSerializer, Alias("platform.parts.snapshot")]
public sealed class PartSnapshot
{
    [Id(0)] public long Seq { get; set; }

    [Id(1)] public SnapshotRecord[] Records { get; set; } = [];

    // The signal entries of the segments this snapshot replaced, so a reopened log still serves them.
    [Id(2)] public LogEntry[] RecentSignals { get; set; } = [];

    // RecentSignals holds every signal entry after this seq; older ones went with the snapshot before.
    [Id(3)] public long SignalsFrom { get; set; }
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
}

public static class PartSegments
{
    public static string SegmentId(PartId part, long firstSeq) => $"{part.Value}/log/{firstSeq}";

    public static string SnapshotId(PartId part, long seq) => $"{part.Value}/snapshot/{seq}";
}
