using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Storage and shutdown wait for nothing on a thread: the brain log, its lease, the log registry and the drain are async end to end.
// The file list is closed (extend it as more code is cleaned); a name must resolve to exactly one file.
public sealed partial class SyncOverAsyncFacts
{
    private static readonly string[] Files = ["LegacyGrainStorage.cs", "LegacySignalJournal.cs", "Drain.cs"];

    // A task's Result read on the line that has just checked IsCompletedSuccessfully is a completed-task read, not a wait.
    [GeneratedRegex(@"\.GetAwaiter\(\)\s*\.GetResult\(\)|\.Wait\(|Thread\.Sleep\(|\.Result\b", RegexOptions.CultureInvariant)]
    private static partial Regex Blocking();

    [Fact]
    public void StorageAndShutdownCodeNeverBlocksOnATask()
    {
        var found = Repository.ProjectFiles("*.cs", path => Files.Contains(Path.GetFileName(path)) && !path.Contains("Tests", StringComparison.Ordinal)).ToArray();
        Assert.Equal(Files.Order(StringComparer.Ordinal), found.Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var hits = found.SelectMany(path => File.ReadLines(path).Select((line, index) => (Path: Repository.Relative(path), Line: index + 1, Text: line.Trim()))
                .Where(entry => !entry.Text.StartsWith("//", StringComparison.Ordinal) && !entry.Text.Contains("IsCompletedSuccessfully", StringComparison.Ordinal)
                    && Blocking().IsMatch(entry.Text)))
            .Select(entry => $"{entry.Path}:{entry.Line}: {entry.Text}").ToArray();

        Assert.True(hits.Length == 0, "Blocking on a task in storage or shutdown code; await it instead." + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }
}
