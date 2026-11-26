namespace IntoChat.OS.Modules.Apps;

// Versions are major.minor.patch and compare numerically, so 1.10.0 follows 1.9.0.
public static class AppVersions
{
    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        for (var part = 0; part < 3; part++)
        {
            var order = a[part].CompareTo(b[part]);
            if (order != 0) { return order; }
        }
        return 0;
    }

    private static int[] Parse(string version)
        => version?.Split('.') is { Length: 3 } parts && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
            ? [.. parts.Select(int.Parse)]
            : throw new FormatException($"'{version}' is not a version of the form major.minor.patch.");
}
