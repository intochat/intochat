namespace IntoChat.OS.Architecture.Tests.Unit;

// Cloud split fence: no tracked source or project file anywhere in the repository may reference the removed Cloud assembly.
public sealed class CloudIsolationFacts
{
    private const string CloudToken = "IntoChat." + "Cloud";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "artifacts", "node_modules", ".git", ".dart_tool", "build", ".vs", ".idea", ".codegraph", "ephemeral",
    };

    private static readonly HashSet<string> SourceAndProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".slnx", ".json", ".yml", ".yaml", ".ps1", ".sh", ".cmd", ".dart", ".razor",
    };

    private static bool IsSourceOrProjectFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return SourceAndProjectExtensions.Contains(Path.GetExtension(name));
    }

    private static IEnumerable<string> ScanFiles()
    {
        var pending = new Stack<string>([Repository.Root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (IsSourceOrProjectFile(file))
                {
                    yield return Repository.Relative(file);
                }
            }
        }
    }

    [Fact]
    public void NoSourceOrProjectFileReferencesCloud()
    {
        var offenders = ScanFiles()
            .Where(relative => SyntaxCache.ReadText(Path.Combine(Repository.Root, relative)).Contains(CloudToken, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"Source/project file(s) contain {CloudToken}:\n" + string.Join('\n', offenders));
    }
}
