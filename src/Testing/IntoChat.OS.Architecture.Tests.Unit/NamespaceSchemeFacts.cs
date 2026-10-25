namespace IntoChat.OS.Architecture.Tests.Unit;

// CORE-05: namespaces follow assemblies; there are no offenders (02-15 renamed the last ones). Rules:
// (a) public types of IntoChat.OS.Modules.* assemblies live under IntoChat.OS.Modules.;
// (b) .Authority namespaces live only in IntoChat.OS.Authority* assemblies;
// (c) no os assembly declares a DigitalBrain namespace (no DigitalBrain source lives in os);
// (d) IntoChat.OS.Framework namespaces live only in the Framework (which keeps its name, R4-03).
public sealed class NamespaceSchemeFacts
{
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
        return ns.StartsWith("IntoChat.OS.Framework", StringComparison.Ordinal) && assembly != "IntoChat.OS.Framework";
    }

    internal static Dictionary<string, int> Capture()
        => ProductionArchitecture.Types.Where(Offends)
            .GroupBy(type => $"{type.Assembly.GetName().Name}: {type.Namespace}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    [Fact]
    public void EveryTypeLivesInANamespaceItsAssemblyOwns()
    {
        var offenders = Capture().Select(entry => $"{entry.Key}: {entry.Value}").ToArray();
        Assert.True(offenders.Length == 0, "Types landed in namespaces their assembly does not own:\n" + string.Join('\n', offenders));
    }
}
