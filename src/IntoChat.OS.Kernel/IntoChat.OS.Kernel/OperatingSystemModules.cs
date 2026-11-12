using System.Reflection;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Http;
using Microsoft.Extensions.DependencyModel;

namespace IntoChat.OS.Kernel;

// The operating system's module list: every assembly the Kernel project references that declares a [Module] type.
// Cloud modules are project references and declare LoadsUnder; the host skips them unless Topology matches.
public static class OperatingSystemModules
{
    public static IReadOnlyList<OsModule> Modules { get; } = Discover();

    // The composition a test process can host by itself: every module that needs no container and loads under local.
    public static IReadOnlyList<OsModule> InProcessModules { get; } = [.. Modules.Where(module => !module.RequiresContainer && module.LoadsUnder("local"))];

    // Module types that are deliberately not in the portable composition: the Windows edge
    // (added by WithWindowsEdge) and the template sample.
    internal static bool IsExcluded(Type type)
        => type == typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule) || type.Name == "SampleModule";

    private static OsModule[] Discover()
    {
        var modules = new List<OsModule>();
        foreach (var assembly in CandidateAssemblies())
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null || !IsProductModule(type) || IsExcluded(type)) { continue; }
                modules.Add(OsModule.FromDigital(type));
            }
        }
        var companions = modules.Where(module => module.Neurons is { } neurons && neurons != module.Type).Select(module => module.Neurons!).ToHashSet();
        modules.RemoveAll(module => companions.Contains(module.Type));
        return [.. modules.OrderBy(module => module.Order).ThenBy(module => module.Id.Value, StringComparer.Ordinal)];
    }

    private static bool IsProductModule(Type type)
    {
        if (!typeof(DigitalBrain.Modules.Module).IsAssignableFrom(type) || type.GetCustomAttribute<DigitalBrain.Modules.ModuleAttribute>() is null) { return false; }
        // The kernel composition carries two product modules of its own: Orleans and the http@1 connector.
        if (type == typeof(IntoChat.OS.Modules.Microsoft.Orleans.OrleansModule)) { return true; }
        if (type == typeof(IntoChat.OS.Kernel.Composition.Capabilities.HttpModule)) { return true; }
        var name = type.Assembly.GetName().Name ?? "";
        return name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) || name.StartsWith("IntoChat.Cloud.", StringComparison.Ordinal);
    }

    // Assemblies named by the Kernel dependency closure, not a directory listing of whatever sits beside the process.
    private static IEnumerable<Assembly> CandidateAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Assembly>();
        var queue = new Queue<Assembly>();
        void Consider(AssemblyName name)
        {
            if (name.Name is null || !IsModuleAssembly(name.Name) || !seen.Add(name.Name)) { return; }
            try
            {
                var loaded = Assembly.Load(name);
                found.Add(loaded);
                queue.Enqueue(loaded);
            }
            catch (FileNotFoundException) { return; }
            catch (FileLoadException) { return; }
            catch (BadImageFormatException) { return; }
        }

        var context = DependencyContext.Load(typeof(OperatingSystemModules).Assembly) ?? DependencyContext.Default;
        if (context is not null)
        {
            foreach (var library in context.RuntimeLibraries) { Consider(new AssemblyName(library.Name)); }
        }
        foreach (var reference in typeof(OperatingSystemModules).Assembly.GetReferencedAssemblies()) { Consider(reference); }
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies()) { Consider(reference); }
        }
        return found;
    }

    private static bool IsModuleAssembly(string? name)
        => name is not null && (name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
            || name.StartsWith("IntoChat.Cloud.", StringComparison.Ordinal)
            || name == "IntoChat.OS.Kernel.Composition");

    // The Windows edge: the composition plus desktop automation of the Windows machine the host runs on.
    // Only a host on Windows opts in; the portable composition never carries it.
    public static IReadOnlyList<OsModule> WithWindowsEdge()
        => [.. Modules, OsModule.FromDigital(typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule))];

    // Composes the given modules (the portable composition by default) and the OS apps.
    public static IntoChatOSServerBuilder AddOperatingSystem(this IntoChatOSServerBuilder server, IReadOnlyList<OsModule>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var selected = modules ?? Modules;
        foreach (var module in selected) { server.AddModule(module); }
        server.AddNeuronModule(typeof(IntoChat.OS.Apps.ShippedShellAppsModule));
        server.ConfigureServices(services =>
        {
            services.AddSingleton<IntoChat.OS.Modules.Postgres.Tables.ITablesSlot, IntoChat.OS.Modules.Postgres.AppTablesSlot>();
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(
                services,
                ServiceDescriptor.Singleton<IntoChat.OS.Modules.Apps.IAppStorageHook, IntoChat.OS.Modules.Postgres.Tables.TableStorageHook>());
            services.AddMeteredHttpPipelines(selected);
            services.AddOperatingSystemApps();
        });
        return server;
    }
}
