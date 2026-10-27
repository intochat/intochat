using System.Text.RegularExpressions;

namespace IntoChat.OS.Kernel.Tests.Integration.Contracts;

// Just enough of a Dart source file to read a wire mirror: a class body, its final fields and the JSON keys it reads.
internal static partial class DartSource
{
    // The shell's shared Dart package, beside the Flutter module's project in the solution.
    public static string Core(string file)
        => File.ReadAllText(Path.Combine(SolutionProjects.DirectoryOf("IntoChat.OS.Modules.Flutter"), "..", "..", "app", "core", "lib/src", file));

    // From the class declaration to the first closing brace at column zero.
    public static string Class(string source, string name)
    {
        var match = Regex.Match(source, $@"^(?:final |abstract |sealed )*class {name}\b.*?^\}}", RegexOptions.Multiline | RegexOptions.Singleline);
        return match.Success ? match.Value : throw new InvalidOperationException($"No Dart class {name}.");
    }

    // A function or method body: from its declaration to the first closing brace at its own indentation.
    public static string Member(string source, string declaration)
    {
        var match = Regex.Match(source, $@"^(?<indent>[ ]*){Regex.Escape(declaration)}.*?^\k<indent>\}}", RegexOptions.Multiline | RegexOptions.Singleline);
        return match.Success ? match.Value : throw new InvalidOperationException($"No Dart member '{declaration}'.");
    }

    // The names a body declares `final Type a, b;` (generic arguments stripped first, so their commas do not split names).
    public static HashSet<string> FinalFields(string body)
    {
        var flat = body;
        while (Generic().IsMatch(flat)) { flat = Generic().Replace(flat, ""); }
        return [.. FinalField().Matches(flat).SelectMany(field => field.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries))];
    }

    // The keys a body reads from a decoded JSON map: json['key'] or session['key'].
    public static HashSet<string> JsonKeys(string body, string map) => [.. Regex.Matches(body, $@"\b{map}\['(?<key>\w+)'\]").Select(key => key.Groups["key"].Value)];

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex Generic();

    [GeneratedRegex(@"^\s*final\s+[\w?]+\s+(?<names>\w+(?:\s*,\s*\w+)*)\s*;", RegexOptions.Multiline)]
    private static partial Regex FinalField();
}
