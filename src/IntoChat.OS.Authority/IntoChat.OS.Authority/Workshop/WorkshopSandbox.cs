namespace IntoChat.OS.Authority.Workshop;

// The Workshop's path rail: every file it writes or builds stays under the configured root and
// never touches version control or configuration that carries secrets.
public static class WorkshopSandbox
{
    private static readonly string[] Denied = [".git", ".env", "appsettings", "secrets"];

    public static bool TryResolve(string root, string path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) { return false; }
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string combined;
        try { combined = Path.GetFullPath(Path.Combine(normalizedRoot, path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { return false; }
        var relative = Path.GetRelativePath(normalizedRoot, combined).Replace('\\', '/');
        foreach (var segment in relative.Split('/'))
        {
            if (Denied.Any(denied => segment.Contains(denied, StringComparison.OrdinalIgnoreCase))) { return false; }
        }
        full = combined;
        return true;
    }
}
