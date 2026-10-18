namespace IntoChat.OS.Microsoft.Roslyn;

public sealed record CommitOutcome(IReadOnlyList<string> WrittenPaths, long SnapshotVersion);
