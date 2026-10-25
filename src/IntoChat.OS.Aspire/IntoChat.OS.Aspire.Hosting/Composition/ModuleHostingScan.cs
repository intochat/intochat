using System.Reflection;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Modules;

namespace IntoChat.OS.Aspire.Hosting;

public static class ModuleHostingScan
{
    // Adapters referenced by the given root assemblies, indexed by the module they claim.
    // An unclaimed module simply has no adapter; a hosting assembly that is referenced but cannot be loaded throws.
    public static IReadOnlyDictionary<ModuleId, IIntoChatOSModuleHosting> Resolve(params Assembly[] roots)
    {
        var adapters = new Dictionary<ModuleId, IIntoChatOSModuleHosting>();
        var seen = new HashSet<string>();
        var queue = new Queue<Assembly>(roots);
        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.Add(assembly.FullName!)) { continue; }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { queue.Enqueue(Assembly.Load(reference)); }
                // A missing hosting adapter silently leaves its module unhosted; any other absence is benign.
                catch (FileNotFoundException missing) when (!IsHostingAssembly(missing.FileName ?? reference.Name)) { continue; }
                catch (FileLoadException) { continue; }
            }
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || !typeof(IIntoChatOSModuleHosting).IsAssignableFrom(type)) { continue; }
                var adapter = (IIntoChatOSModuleHosting)Activator.CreateInstance(type)!;
                adapters[adapter.Module] = adapter;
            }
        }
        return adapters;
    }

    private static bool IsHostingAssembly(string? name)
        => name is not null && name.Split(',')[0].EndsWith(".Aspire.Hosting", StringComparison.Ordinal);
}
