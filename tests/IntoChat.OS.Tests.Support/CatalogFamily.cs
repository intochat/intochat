using System.Reflection;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Authoring;

namespace IntoChat.OS.Modules.Apps;

// The catalog and the modules cut out of it whose neurons the catalog and the shell apps call (unit authoring: flow.build and
// ino.synapses). A test host that hosts the catalog hosts them too, as the product does. A module that leaves the catalog later is
// added here once, so the hosts keep what they had.
public static class CatalogFamily
{
    public static IReadOnlyList<Type> Modules { get; } = [typeof(AppsModule), typeof(AuthoringModule)];

    public static IReadOnlyList<Assembly> Assemblies { get; } = [.. Modules.Select(module => module.Assembly)];

    public static TestOS RegisterCatalog(this TestOS os)
    {
        ArgumentNullException.ThrowIfNull(os);
        foreach (var assembly in Assemblies) { os.Register(assembly); }
        return os;
    }
}
