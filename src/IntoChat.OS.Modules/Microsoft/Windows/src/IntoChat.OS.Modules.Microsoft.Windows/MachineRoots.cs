namespace IntoChat.OS.Modules.Microsoft.Windows;

// Paths resolve against the first root when relative and must land inside some root; "C:\root2" is not inside "C:\root".
internal static class MachineRoots
{
    // A full path without a trailing separator, except a drive root, which keeps it ("E:\"; "E:" means E's current folder).
    // Windows paths are case-insensitive; elsewhere "/Root" and "/root" are different folders.
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string Resolve(string[] roots, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (roots.Length == 0) { throw new UnauthorizedAccessException("No folder on this machine is allowed yet."); }
        var full = Normalize(Path.IsPathRooted(path) ? path : Path.Combine(roots[0], path));
        foreach (var root in roots)
        {
            var inside = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (string.Equals(full, root, Comparison)
                || full.StartsWith(inside, Comparison))
            {
                RequireNoLinks(full);
                return full;
            }
        }
        throw new UnauthorizedAccessException($"'{path}' is outside the folders allowed on this machine.");
    }

    // Lexical containment alone admits a junction or symbolic link whose target is outside the allowed root.
    // Check every existing component, including the root's parents, and repeat before committing a write.
    internal static void RequireNoLinks(string path)
    {
        var full = Normalize(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { throw new UnauthorizedAccessException("Symbolic links and junctions are outside the allowed storage paths."); }
        }
    }

    // The longest allowed root holding an already resolved full path.
    public static string Containing(string[] roots, string full) => roots
        .Where(root => string.Equals(full, root, Comparison) || full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison))
        .MaxBy(root => root.Length) ?? throw new UnauthorizedAccessException($"'{full}' is outside the folders allowed on this machine.");
}
