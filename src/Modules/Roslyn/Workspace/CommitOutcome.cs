namespace IntoChat.OS.Modules.Microsoft.Roslyn;

internal sealed record CommitOutcome(IReadOnlyList<string> WrittenPaths, long SnapshotVersion);
