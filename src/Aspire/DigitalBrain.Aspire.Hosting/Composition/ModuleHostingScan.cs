using System.Reflection;
using DigitalBrain;
using DigitalBrain.Kernel;

namespace DigitalBrain.Aspire.Hosting;

public static class ModuleHostingScan
{
    // Adapters referenced by the given root assemblies, indexed by the module they claim.
    // An unclaimed module simply has no adapter — absence of the package is not an error.
    public static IReadOnlyDictionary<ModuleId, IDigitalBrainModuleHosting> Resolve(params Assembly[] roots)
    {
        var adapters = new Dictionary<ModuleId, IDigitalBrainModuleHosting>();
        var seen = new HashSet<string>();
        var queue = new Queue<Assembly>(roots);
        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.Add(assembly.FullName!)) { continue; }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { queue.Enqueue(Assembly.Load(reference)); }
                catch (FileNotFoundException) { } catch (FileLoadException) { }
            }
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || !typeof(IDigitalBrainModuleHosting).IsAssignableFrom(type)) { continue; }
                var adapter = (IDigitalBrainModuleHosting)Activator.CreateInstance(type)!;
                adapters[adapter.Module] = adapter;
            }
        }
        return adapters;
    }
}
