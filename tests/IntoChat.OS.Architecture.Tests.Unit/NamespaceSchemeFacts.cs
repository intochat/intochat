namespace IntoChat.OS.Architecture.Tests.Unit;

// CORE-05: namespaces follow assemblies. Rules:
// (a) public types of IntoChat.OS.Modules.* assemblies live under IntoChat.OS.Modules., except the identity module
//     (it keeps the Authority, Connections, Integrations and Confirmation namespaces) and the Apps module (IntoChat.Files);
// (b) those kept namespaces live only in the identity module and its contracts;
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
            if (IdentityAssembly(assembly) && KeptIdentityNamespace(ns)) { return false; }
            if ((assembly is "IntoChat.OS.Modules.Catalog" or "IntoChat.OS.Modules.Apps") && (ns == "IntoChat.Files" || ns.StartsWith("IntoChat.Files.", StringComparison.Ordinal))) { return false; }
            return true;
        }
        if (KeptIdentityNamespace(ns) && !IdentityAssembly(assembly))
        {
            return true;
        }
        // The DigitalBrain namespaces belong to the DigitalBrain packages (loaded as the lowest kernel layer) and to no os assembly.
        if (ns.StartsWith("DigitalBrain", StringComparison.Ordinal))
        {
            return !assembly.StartsWith("DigitalBrain", StringComparison.Ordinal);
        }
        return ns.StartsWith("IntoChat.OS.Framework", StringComparison.Ordinal) && assembly != "IntoChat.OS.Framework";
    }

    private static bool IdentityAssembly(string assembly)
        => assembly is "IntoChat.OS.Framework" or "IntoChat.OS.Modules.Identity" or "IntoChat.OS.Modules.Identity.Contracts";

    private static bool KeptIdentityNamespace(string ns)
        => ns.StartsWith("IntoChat.OS.Authority", StringComparison.Ordinal)
            || ns.StartsWith("IntoChat.OS.Connections", StringComparison.Ordinal)
            || ns.StartsWith("IntoChat.OS.Integrations", StringComparison.Ordinal)
            || ns.StartsWith("IntoChat.OS.Confirmation", StringComparison.Ordinal);

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
