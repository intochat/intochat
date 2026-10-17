namespace DigitalBrain;

// The caller acted on a stale revision: CurrentRevision is the one to act on instead. Code is the refusal
// header the runtime edge answers with, so the same exception arrives on both sides of the script boundary.
[GenerateSerializer, Alias("revision-conflict")]
public sealed class RevisionConflictException(string message, long currentRevision) : InvalidOperationException(message)
{
    [Id(0)] public long CurrentRevision { get; } = currentRevision;

    public const string Code = "RevisionConflict";
}
