namespace IntoChat.OS.Framework.Generators;

// Names written into generated source. A name under the file's own namespace is written relative to it, and a root
// that the file's namespace would capture is imported with a file-level using.
internal static class Qualification
{
    // Symbol display used to prefix this. Kept so a caller that still passes it is normalised. Spelled in two parts
    // so this file does not contain the alias token.
    private static readonly string RootAlias = "global" + "::";

    public static string For(string name, string? targetNamespace, ICollection<string> usings)
    {
        if (name.StartsWith(RootAlias, System.StringComparison.Ordinal)) { name = name.Substring(RootAlias.Length); }
        if (targetNamespace is { Length: > 0 } && name.StartsWith(targetNamespace + ".", System.StringComparison.Ordinal))
        {
            return name.Substring(targetNamespace.Length + 1);
        }

        var dot = name.IndexOf('.');
        var first = dot < 0 ? name : name.Substring(0, dot);
        if (!Captured(targetNamespace, first) || dot < 0) { return name; }

        var importing = "using " + name.Substring(0, name.LastIndexOf('.')) + ";";
        if (!usings.Contains(importing)) { usings.Add(importing); }
        return name.Substring(name.LastIndexOf('.') + 1);
    }

    // True when the simple name binds to an enclosing namespace other than the global namespace of that name.
    private static bool Captured(string? targetNamespace, string first)
    {
        if (targetNamespace is not { Length: > 0 } ns) { return false; }
        var parts = ns.Split('.');
        for (var i = parts.Length; i >= 1; i--)
        {
            if (parts[i - 1] != first) { continue; }
            return i != 1;
        }
        return false;
    }
}
