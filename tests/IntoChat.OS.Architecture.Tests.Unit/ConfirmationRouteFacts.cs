namespace IntoChat.OS.Architecture.Tests.Unit;

// A confirmation has one answer: POST /brains/{id}/confirmations/{**planId} {approve}, a direct person only. The screens route that
// also answered one, and the MCP tool that called the same handler, are deleted; nothing in the repository names either again (code,
// clients, tests, docs). The names are spelled by concatenation so this file never matches itself.
public sealed class ConfirmationRouteFacts
{
    private static readonly string[] DeletedNames = ["screens/" + "approvals", "screen" + "_approve"];

    private static readonly HashSet<string> Scanned = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".dart", ".md", ".json", ".yaml", ".yml", ".ts", ".js" };

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "artifacts", "build", ".dart_tool", "node_modules", "TestResults", ".codegraph",
    };

    [Fact]
    public void Nothing_names_the_deleted_screen_approval_route()
    {
        var offenders = Files(Repository.Root)
            .Where(file => !file.EndsWith(nameof(ConfirmationRouteFacts) + ".cs", StringComparison.Ordinal))
            .SelectMany(file =>
            {
                var text = SyntaxCache.ReadText(file);
                return DeletedNames
                    .Where(name => text.Contains(name, StringComparison.Ordinal))
                    .Select(name => $"{Path.GetRelativePath(Repository.Root, file)}: {name}");
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0, "A confirmation is answered on /confirmations/{planId} only:\n" + string.Join('\n', offenders));
    }

    private static IEnumerable<string> Files(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (Scanned.Contains(Path.GetExtension(file))) { yield return file; }
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Skipped.Contains(Path.GetFileName(child))) { continue; }
            foreach (var file in Files(child)) { yield return file; }
        }
    }
}
