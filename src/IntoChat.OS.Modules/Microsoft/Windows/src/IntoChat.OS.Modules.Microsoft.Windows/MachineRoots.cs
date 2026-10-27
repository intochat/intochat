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
                || full.StartsWith(inside, Comparison)) { return full; }
        }
        throw new UnauthorizedAccessException($"'{path}' is outside the folders allowed on this machine.");
    }

    // The longest allowed root holding an already resolved full path.
    public static string Containing(string[] roots, string full) => roots
        .Where(root => string.Equals(full, root, Comparison) || full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison))
        .MaxBy(root => root.Length) ?? throw new UnauthorizedAccessException($"'{full}' is outside the folders allowed on this machine.");
}
