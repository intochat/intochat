using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Authority.Compute;

[GenerateSerializer, Alias("compute.trace-journal-state")]
internal sealed class TraceJournalState
{
    [Id(0)] public TraceRevision[] Revisions { get; set; } = [];
    [Id(1)] public string CursorSecret { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    [Id(2)] public DateTimeOffset CreatedAt { get; set; }
    [Id(3)] public bool Incomplete { get; set; }
    [Id(4)] public bool Expired { get; set; }
    [Id(5)] public long Watermark { get; set; }
}

[GenerateSerializer, Alias("compute.trace-revision")]
internal sealed record TraceRevision([property: Id(0)] long Sequence, [property: Id(1)] Cost Cost);
