using IntoChat.OS;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Modules;

namespace IntoChat.OS.Kernel;

// The one place the operating system's module composition is defined: discovered by reflection, not listed.
// Every host of the OS registers modules through it. A distribution is a diff against this
// composition, not a copy of it. The Workshop appends modules through Generated().
public static partial class OsCatalog
{
    public static IReadOnlyList<Module> Modules { get; } = [.. Discover(), .. Generated()];

    // The composition a test process can host by itself: every module that needs no container.
    public static IReadOnlyList<Module> InProcessModules { get; } = [.. Modules.Where(module => !module.RequiresContainer)];

    // Module types that are deliberately not in the portable composition: the Windows edge
    // (added by WithWindowsEdge) and the template sample.
    internal static bool IsExcluded(Type type)
        => type == typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule) || type.Name == "SampleModule";

    // Every public, concrete Module with a public parameterless constructor in the module assemblies the
    // OS ships (the Kernel's reference closure and the module assemblies beside it), minus the exclusions,
    // ordered by Module.Order and then by id. A new module appears here without editing this file.
    private static Module[] Discover()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<System.Reflection.Assembly>([typeof(OsCatalog).Assembly]);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "IntoChat.OS.Modules.*.dll"))
        {
            try { queue.Enqueue(System.Reflection.Assembly.Load(System.Reflection.AssemblyName.GetAssemblyName(file))); }
            catch (BadImageFormatException) { continue; }
            catch (FileLoadException) { continue; }
        }
        var modules = new List<Module>();
        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.Add(assembly.FullName!)) { continue; }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { queue.Enqueue(System.Reflection.Assembly.Load(reference)); }
                catch (FileNotFoundException) { continue; }
                catch (FileLoadException) { continue; }
            }
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || !typeof(Module).IsAssignableFrom(type) || IsExcluded(type)) { continue; }
                if (type.GetConstructor(Type.EmptyTypes) is null) { continue; }
                modules.Add((Module)Activator.CreateInstance(type)!);
            }
        }
        return [.. modules.OrderBy(module => module.Order).ThenBy(module => module.Id.ToString(), StringComparer.Ordinal)];
    }

    // Modules the Workshop has added to this OS. Declared here, implemented in
    // OsCatalog.Generated.cs so the composition grows without hand-editing this file.
    private static partial Module[] Generated();

    // The Windows edge: the composition plus desktop automation of the Windows machine the host runs on.
    // Only a host on Windows opts in; the portable composition never carries it.
    public static IReadOnlyList<Module> WithWindowsEdge() => [.. Modules, new IntoChat.OS.Modules.Microsoft.Windows.WindowsModule()];

    // Composes the given modules (the portable composition by default) and the OS apps.
    public static IntoChatOSServerBuilder AddOperatingSystem(this IntoChatOSServerBuilder server, IReadOnlyList<Module>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var module in modules ?? Modules) { server.AddModule(module); }
        // The OS is the composition plus its apps; a distribution cannot forget them.
        server.ConfigureServices(services => services.AddOperatingSystemApps());
        return server;
    }
}
