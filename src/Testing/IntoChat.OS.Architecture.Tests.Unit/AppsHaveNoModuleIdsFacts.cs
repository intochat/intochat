using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// TEST-03: first-party apps under features/os name capabilities, never modules or vendors. Hits are module ids as
// whole words inside string literals, counted per file; AppsModuleIdBaseline.json holds today's and only shrinks.
public sealed partial class AppsHaveNoModuleIdsFacts
{
    internal const string BaselineFile = "AppsModuleIdBaseline.json";

    internal static readonly string[] ModuleIds = ["gmail", "google", "microsoft", "outlook", "salesforce", "supabase", "github", "azure", "openai", "anthropic"];

    [GeneratedRegex(@"""(?:[^""\\\r\n]|\\.)*""", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    private static readonly Regex ModuleId = new(@"\b(?:" + string.Join('|', ModuleIds) + @")\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static Dictionary<string, int> Capture()
        => Repository.SourceFiles(Path.Combine(Repository.Root, "features", "os"), "*.cs")
            .Select(path => (Key: Repository.Relative(path), Count: StringLiteral().Matches(File.ReadAllText(path)).Sum(literal => ModuleId.Count(literal.Value))))
            .Where(file => file.Count > 0)
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .ToDictionary(file => file.Key, file => file.Count, StringComparer.Ordinal);

    [Fact]
    public void ModuleIdsInAppsOnlyShrink()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(CaptureWriter.Path(BaselineFile)))!;
        var current = Capture();
        var grew = current.Where(file => file.Value > baseline.GetValueOrDefault(file.Key))
            .Select(file => $"{file.Key}: {baseline.GetValueOrDefault(file.Key)} -> {file.Value}").ToArray();
        Assert.True(grew.Length == 0, "Apps gained module ids; name the capability instead:\n" + string.Join('\n', grew));
        var shrunk = baseline.Where(file => current.GetValueOrDefault(file.Key) < file.Value)
            .Select(file => $"{file.Key}: {file.Value} -> {current.GetValueOrDefault(file.Key)}").ToArray();
        Assert.True(shrunk.Length == 0, "Module ids in apps went down; regenerate with scripts/capture-all.ps1:\n" + string.Join('\n', shrunk));
    }

    [Fact(Explicit = true)]
    public void CaptureAppsModuleIdBaseline() => CaptureWriter.Write(BaselineFile, Capture());
}
