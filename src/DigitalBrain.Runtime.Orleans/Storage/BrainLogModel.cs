using Orleans;

namespace DigitalBrain.Runtime.Orleans;

internal enum BrainRecordChange { None, Write, Clear }

/// <summary>
/// Byte layout of a brain-log segment and snapshot. Both start with "DBLG" and this version byte. Only <see cref="Json"/> is
/// written or read; <see cref="Legacy"/> and <see cref="Framed"/> are the os formats, refused on open (SAFE-01 clean break).
/// </summary>
internal enum BrainLogFormat : byte { Legacy = 0, Framed = 1, Json = 2 }

// Framing records are runtime-internal and keep the Orleans encoding; signal and state payloads inside them are JSON.
[GenerateSerializer, Alias("storage.brain-record")]
internal sealed record BrainRecord([property: Id(0)] byte[] Record, [property: Id(1)] long ETag);

/// <summary>One journaled signal: its frozen <c>[Signal]</c> alias and its JSON through RuntimeProtocol.Json.</summary>
[GenerateSerializer, Alias("storage.journaled-signal")]
internal sealed record JournaledSignalPayload([property: Id(0)] string Type, [property: Id(1)] byte[] Json);

/// <summary>A publisher's retention floor and last sequence; every sequence at or below the floor is trimmed.</summary>
[GenerateSerializer, Alias("storage.publisher-mark")]
internal sealed record PublisherMark([property: Id(0)] string Publisher, [property: Id(1)] long Floor, [property: Id(2)] long Last);

/// <summary>
/// One durable brain change: a state record and the signals its publisher emitted with it. <see cref="FirstSignal"/> is the
/// publisher's own sequence of the first signal, <see cref="FirstPosition"/> its brain-wide position.
/// </summary>
[GenerateSerializer, Alias("storage.brain-entry")]
internal sealed record BrainEntry(
    [property: Id(0)] long Sequence,
    [property: Id(1)] string GrainId,
    [property: Id(2)] string StateName,
    [property: Id(3)] BrainRecordChange Change,
    [property: Id(4)] byte[] Record,
    [property: Id(5)] string Publisher,
    [property: Id(6)] JournaledSignalPayload[] Signals,
    [property: Id(7)] long FirstSignal,
    [property: Id(8)] long FirstPosition,
    [property: Id(9)] long AtUnixMs);

[GenerateSerializer, Alias("storage.brain-snapshot")]
internal sealed record BrainSnapshot(
    [property: Id(0)] long Sequence,
    [property: Id(1)] long Position,
    [property: Id(2)] Dictionary<string, Dictionary<string, BrainRecord>> Records,
    [property: Id(3)] BrainEntry[] Signals,
    [property: Id(4)] PublisherMark[] Publishers);

/// <summary>A held signal as the log reads it back, still encoded.</summary>
internal sealed record StoredSignal(string Publisher, long Sequence, long Position, long AtUnixMs, JournaledSignalPayload Payload);

/// <summary>Held signals after a sequence or position, with the floor and last value of that numbering.</summary>
internal sealed record StoredSlice(long Floor, long Last, StoredSignal[] Held);
