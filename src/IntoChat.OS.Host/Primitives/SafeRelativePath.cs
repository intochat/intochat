namespace IntoChat.OS.Host.Primitives;

/// <summary>
/// Validated forward-slash relative path that cannot escape a storage or workspace root.
/// </summary>
[GenerateSerializer, Alias("os.safe-relative-path")]
public readonly record struct SafeRelativePath
{
    [Id(0)]
    public string Value { get; init; }

    private SafeRelativePath(string value) => Value = value;
    public static bool TryParse(string? raw, out SafeRelativePath path)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0', StringComparison.Ordinal))
        {
            path = default;
            return false;
        }

        var normalized = raw.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0
            || normalized.StartsWith('/')
            || normalized.Contains(':', StringComparison.Ordinal))
        {
            path = default;
            return false;
        }

        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            path = default;
            return false;
        }

        path = new SafeRelativePath(string.Join('/', segments));
        return true;
    }

    public static SafeRelativePath Parse(string raw)
        => TryParse(raw, out var path)
            ? path
            : throw new ArgumentException($"Invalid or unsafe relative path '{raw}'.", nameof(raw));

    public string ResolveUnder(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        var combined = Path.GetFullPath(Path.Combine(fullRoot, Value.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(fullRoot, comparison))
        {
            throw new InvalidOperationException($"Resolved path '{Value}' escapes root directory.");
        }

        return combined;
    }

    public override string ToString() => Value ?? string.Empty;
}
