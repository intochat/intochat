using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// 06-07b (R4-17): module neurons reach still-legacy grains through IntoChat.OS's ILegacyNeurons while families migrate. Only the
// kernel composition implements it, and its users per file only shrink (LegacyNeuronsOwners.json); 06-08 deletes the seam,
// which leaves the file empty.
public sealed partial class LegacyNeuronsFacts
{
    internal const string FileName = "LegacyNeuronsOwners.json";
    private const string Composition = "IntoChat.OS.Kernel.Composition";

    [GeneratedRegex(@"\bILegacyNeurons\b|\bLegacyNeuronAccess\b|\bContext\.Legacy<", RegexOptions.CultureInvariant)]
    private static partial Regex Use();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+\w+[^{;]*?:[^{;]*\bILegacyNeurons\b", RegexOptions.CultureInvariant)]
    private static partial Regex Implementation();

    private static IEnumerable<(string Key, string Text)> Sources()
        => Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Repository.ProjectKey(path), File.ReadAllText(path)));

    // The seam's own declaration, its implementation and the facts that name it are not owners.
    private static bool Seam(string key)
        => key == "IntoChat.OS/Runtime/LegacyNeurons.cs" || key.StartsWith(Composition + "/", StringComparison.Ordinal)
            || key.StartsWith("IntoChat.OS.Architecture.Tests.Unit/", StringComparison.Ordinal);

    internal static Dictionary<string, int> Current()
        => Sources().Where(file => !Seam(file.Key))
            .Select(file => (file.Key, Count: Use().Count(file.Text)))
            .Where(file => file.Count > 0)
            .ToDictionary(file => file.Key, file => file.Count, StringComparer.Ordinal);

    [Fact]
    public void OnlyTheKernelCompositionImplementsLegacyNeurons()
    {
        var offenders = Sources().Where(file => !file.Key.StartsWith(Composition + "/", StringComparison.Ordinal) && Implementation().IsMatch(file.Text))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "ILegacyNeurons is implemented by the kernel composition only:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void LegacyNeuronsOwnersOnlyShrink() => NeuronMigrationFacts.CountsOnlyShrink(FileName, Current(),
        "New calls from module neurons to legacy grains; migrate the callee, or deliberately raise the ratchet");

    [Fact(Explicit = true)]
    public void CaptureLegacyNeuronsOwners() => CaptureWriter.Write(FileName, CaptureWriter.Sorted(FileName, Current().OrderBy(entry => entry.Key, StringComparer.Ordinal)));
}
