using System.Text.Json;

namespace IntoChat.OS.Architecture.Tests.Unit;

// CORE-05: namespaces follow assemblies. Offending types are counted per "assembly: namespace" and pinned in
// NamespaceSchemeBaseline.json; 02-15 drives it to empty. Rules:
// (a) public types of IntoChat.OS.Modules.* assemblies live under IntoChat.OS.Modules.;
// (b) .Authority namespaces live only in IntoChat.OS.Authority* assemblies;
// (c) no os assembly declares a DigitalBrain namespace (no DigitalBrain source lives in os);
// (d) IntoChat.OS.Framework namespaces live only in the Framework (which keeps its name, R4-03).
public sealed class NamespaceSchemeFacts
{
    internal const string BaselineFile = "NamespaceSchemeBaseline.json";

    internal static bool Offends(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        var ns = type.Namespace ?? "";
        if (assembly.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) && type.IsPublic && !ns.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
        {
            return true;
        }
        if (ns.Contains(".Authority", StringComparison.Ordinal) && !assembly.StartsWith("IntoChat.OS.Authority", StringComparison.Ordinal))
        {
            return true;
        }
        if (ns.StartsWith("DigitalBrain", StringComparison.Ordinal))
        {
            return true;
        }
        // Connections exempt from rule (d) until 02-15 re-namespaces the moved Framework folders.
        return ns.StartsWith("IntoChat.OS.Framework", StringComparison.Ordinal) && assembly is not ("IntoChat.OS.Framework" or "IntoChat.OS.Connections");
    }

    internal static Dictionary<string, int> Capture()
        => ProductionArchitecture.Types.Where(Offends)
            .GroupBy(type => $"{type.Assembly.GetName().Name}: {type.Namespace}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    [Fact]
    public void NamespaceOffendersOnlyShrink()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(CaptureWriter.Path(BaselineFile)))!;
        var current = Capture();
        var grew = current.Where(entry => entry.Value > baseline.GetValueOrDefault(entry.Key))
            .Select(entry => $"{entry.Key}: {baseline.GetValueOrDefault(entry.Key)} -> {entry.Value}").ToArray();
        Assert.True(grew.Length == 0, "Types landed in namespaces their assembly does not own:\n" + string.Join('\n', grew));
        var shrunk = baseline.Where(entry => current.GetValueOrDefault(entry.Key) < entry.Value)
            .Select(entry => $"{entry.Key}: {entry.Value} -> {current.GetValueOrDefault(entry.Key)}").ToArray();
        Assert.True(shrunk.Length == 0, "Namespace offenders went down; regenerate with scripts/capture-all.ps1:\n" + string.Join('\n', shrunk));
    }

    [Fact(Explicit = true)]
    public void CaptureNamespaceSchemeBaseline() => CaptureWriter.Write(BaselineFile, Capture());
}
