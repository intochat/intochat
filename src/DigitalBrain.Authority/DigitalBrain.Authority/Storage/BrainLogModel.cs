namespace DigitalBrain.Authority.Storage;

public enum BrainRecordChange { None, Write, Clear }

// Byte layout of a brain-log segment. Framed segments start with "DBLG" and this version byte.
public enum BrainLogFormat : byte { Legacy = 0, Framed = 1 }

internal sealed record BrainExport(int FormatVersion, string BrainScope, long Sequence, BrainExportRecord[] Records, int OmittedRecords);
internal sealed record BrainExportRecord(string GrainId, string StateName, string Type, System.Text.Json.JsonElement Data);

[GenerateSerializer, Alias("storage.brain-record")]
public sealed record BrainRecord([property: Id(0)] byte[] Record, [property: Id(1)] long ETag);

[GenerateSerializer, Alias("storage.brain-entry")]
internal sealed record BrainEntry(
    [property: Id(0)] long Sequence,
    [property: Id(1)] string GrainId,
    [property: Id(2)] string StateName,
    [property: Id(3)] BrainRecordChange Change,
    [property: Id(4)] byte[] Record,
    [property: Id(5)] Signal[] Signals,
    [property: Id(6)] long FirstSignal);

[GenerateSerializer, Alias("storage.brain-snapshot")]
internal sealed record BrainSnapshot(
    [property: Id(0)] long Sequence,
    [property: Id(1)] long LastSignal,
    [property: Id(2)] Dictionary<string, Dictionary<string, BrainRecord>> Records,
    [property: Id(3)] BrainEntry[] Signals,
    [property: Id(4)] BrainLogFormat Format = BrainLogFormat.Legacy);
