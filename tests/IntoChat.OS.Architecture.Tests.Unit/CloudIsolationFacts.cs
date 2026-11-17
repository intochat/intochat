namespace IntoChat.OS.Architecture.Tests.Unit;

// Cloud split fence: no tracked source or project file outside the Cloud directories
// (src Cloud module/host projects and Cloud unit tests) may reference the Cloud assembly beyond the shrinking allowlist.
public sealed class CloudIsolationFacts
{
    private const string SourceSegment = "s" + "rc";
    private const string CloudToken = "IntoChat." + "Cloud";

    private static readonly string[] ExcludedPrefixes =
    [
        SourceSegment + "/" + CloudToken + "/",
        SourceSegment + "/" + CloudToken + ".Host/",
        "tests/" + CloudToken + ".Tests.Unit/",
    ];

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

    private static bool IsExcluded(string relativePath)
        => ExcludedPrefixes.Any(prefix => relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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
                if (!IsSourceOrProjectFile(file)) { continue; }
                var relative = Repository.Relative(file);
                if (!IsExcluded(relative))
                {
                    yield return relative;
                }
            }
        }
    }

    [Fact]
    public void NoUnlistedSourceOrProjectFileReferencesCloud()
    {
        var allowlistPath = Path.Combine(
            Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory,
            "CloudReferenceAllowlist.txt");
        Assert.True(File.Exists(allowlistPath), "Missing allowlist: " + allowlistPath);

        var allowlistEntries = File.ReadAllLines(allowlistPath)
            .Select(line => line.Trim().Replace('\\', '/'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        Assert.True(allowlistEntries.Length <= 23, $"CloudReferenceAllowlist.txt may have at most 23 entries, found {allowlistEntries.Length}.");

        var allowlist = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (var entry in allowlistEntries)
        {
            if (!allowlist.Add(entry))
            {
                duplicates.Add(entry);
            }
        }
        Assert.True(duplicates.Count == 0, "Duplicate entries in CloudReferenceAllowlist.txt:\n" + string.Join('\n', duplicates));

        var referencing = ScanFiles()
            .Where(relative => File.ReadAllText(Path.Combine(Repository.Root, relative)).Contains(CloudToken, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var unexpected = referencing.Except(allowlist, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Length == 0,
            $"Unlisted source/project file(s) outside Cloud directories contain {CloudToken}:\n" + string.Join('\n', unexpected));

        var stale = allowlist.Except(referencing, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0,
            "Allowlist entries no longer reference Cloud or were moved/deleted; remove them from CloudReferenceAllowlist.txt so it only shrinks:\n" + string.Join('\n', stale));
    }
}
