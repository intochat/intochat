namespace IntoChat.OS.Architecture.Tests.Unit;

// CORE-05: namespaces follow assemblies. Rules:
// (a) public types of IntoChat.OS.Modules.* assemblies live under IntoChat.OS.Modules. (the identity module included,
//     since the 2026-11-20 break-up gave its namespaces its folders);
// (b) the retired namespaces of the old Framework and Authority projects stay retired: no assembly declares them;
// (c) no os assembly declares a DigitalBrain namespace (no DigitalBrain source lives in os).
public sealed class NamespaceSchemeFacts
{
    private static readonly string[] Retired =
    [
        "IntoChat.OS.Authority", "IntoChat.OS.Connections", "IntoChat.OS.Integrations", "IntoChat.OS.Confirmation", "IntoChat.OS.Framework",
    ];

    internal static bool Offends(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        var ns = type.Namespace ?? "";
        if (assembly.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
            && type.IsPublic
            && !ns.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
            && !(assembly == "IntoChat.OS.Modules.Plugins"
                && (ns == "IntoChat.Muncly" || ns.StartsWith("IntoChat.Muncly.", StringComparison.Ordinal)
                    || ns == "IntoChat.Treg" || ns.StartsWith("IntoChat.Treg.", StringComparison.Ordinal))))
        {
            return true;
        }
        if (Retired.Any(retired => ns == retired || ns.StartsWith(retired + ".", StringComparison.Ordinal)))
        {
            return true;
        }
        // The DigitalBrain namespaces belong to the DigitalBrain packages (loaded as the lowest kernel layer) and to no os assembly.
        return ns.StartsWith("DigitalBrain", StringComparison.Ordinal) && !assembly.StartsWith("DigitalBrain", StringComparison.Ordinal);
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
