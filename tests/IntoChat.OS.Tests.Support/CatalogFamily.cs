using System.Reflection;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Authoring;
using IntoChat.OS.Modules.Diagnostics;
using IntoChat.OS.Modules.Runs;

namespace IntoChat.OS.Modules.Apps;

// The catalog and the modules cut out of it whose neurons the catalog and the shell apps call (unit authoring: flow.build and
// ino.synapses; diagnostics: creator.reads and trace.reader; runs: engineer.runs). In Wave D they share IntoChat.OS.Modules.Workspace
// with FlutterModule under disjoint module namespaces, so RegisterCatalog registers only the catalog-family module namespaces
// (keeping Flutter test doubles such as TestWorkspaceNeuron or fake ComputeNeuron free to register alongside it).
public static class CatalogFamily
{
    public static IReadOnlyList<Type> Modules { get; } = [typeof(AppsModule), typeof(AuthoringModule), typeof(DiagnosticsModule), typeof(RunsModule)];

    public static IReadOnlyList<Assembly> Assemblies { get; } = [.. Modules.Select(module => module.Assembly).Distinct()];

    public static TestOS RegisterCatalog(this TestOS os)
    {
        ArgumentNullException.ThrowIfNull(os);
        foreach (var module in Modules)
        {
            RegisterModule(os, module);
        }
        return os;
    }

    private static readonly MethodInfo RegisterGeneric = typeof(TestOS)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(static m => m.Name == nameof(TestOS.Register) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    public static TestOS RegisterModule<TModule>(this TestOS os)
        => RegisterModule(os, typeof(TModule));

    public static TestOS RegisterModule(this TestOS os, Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(moduleType);
        var prefix = moduleType.Namespace;
        foreach (var type in moduleType.Assembly.GetTypes())
        {
            if (type is not { IsAbstract: false, IsClass: true }
                || !typeof(Neuron).IsAssignableFrom(type)
                || !type.IsDefined(typeof(NeuronAttribute), inherit: false))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(prefix)
                && !string.Equals(type.Namespace, prefix, StringComparison.Ordinal)
                && !(type.Namespace?.StartsWith(prefix + ".", StringComparison.Ordinal) ?? false))
            {
                continue;
            }

            RegisterGeneric.MakeGenericMethod(type).Invoke(os, null);
        }

        return os;
    }
}
