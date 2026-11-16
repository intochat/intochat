namespace IntoChat.OS.Modules.Apps;

// The paths of a file share. A share names one folder or one file of the workspace mount; a request names a path
// relative to it. Both are judged segment by segment, never by string prefix, so "/work/a" does not contain "/work/ab".
public static class SharePath
{
    // Only the brain's own workspace is shareable; the local disk and connected drives are not.
    public const string Mount = "work";

    // The canonical form "/work/<segments>" of a share's path.
    public static string Normalize(string? sharePath)
    {
        var segments = Segments(sharePath, nameof(sharePath), allowAbsolute: true);
        if (segments.Length < 2 || !string.Equals(segments[0], Mount, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A share names a folder or file under /{Mount}.", nameof(sharePath));
        }

        return "/" + string.Join('/', segments);
    }

    // The canonical path a request under the share names: the share itself for an empty relative path, otherwise a path
    // beneath it. A share of one file has nothing beneath it.
    public static string Resolve(string sharePath, bool isFolder, string? relativePath)
    {
        var root = Normalize(sharePath);
        if (string.IsNullOrWhiteSpace(relativePath)) { return root; }
        if (!isFolder) { throw new ArgumentException("A shared file has no paths beneath it.", nameof(relativePath)); }

        var segments = Segments(relativePath, nameof(relativePath), allowAbsolute: false);
        return root + "/" + string.Join('/', segments);
    }

    // Whether the canonical path is the canonical share path or beneath it.
    public static bool IsInside(string sharePath, string path)
    {
        ArgumentNullException.ThrowIfNull(sharePath);
        ArgumentNullException.ThrowIfNull(path);
        var share = sharePath.Split('/');
        var candidate = path.Split('/');
        return candidate.Length >= share.Length && share.AsSpan().SequenceEqual(candidate.AsSpan(0, share.Length));
    }

    private static string[] Segments(string? raw, string parameter, bool allowAbsolute)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0', StringComparison.Ordinal) || raw.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The path is empty or holds a character a share path may not.", parameter);
        }

        var normalized = raw.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            if (!allowAbsolute) { throw new ArgumentException("A path under a share is relative to it.", parameter); }
            normalized = normalized[1..];
        }
        if (normalized.EndsWith('/')) { normalized = normalized[..^1]; }

        var segments = normalized.Split('/');
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0 || segment is "." or ".." || segment != segment.Trim()))
        {
            throw new ArgumentException("The path holds an empty, '.' or '..' segment.", parameter);
        }

        return segments;
    }
}
