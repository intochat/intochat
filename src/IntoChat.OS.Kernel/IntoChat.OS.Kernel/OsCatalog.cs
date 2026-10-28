using System.Reflection;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework.Modules;
using FrameworkModule = IntoChat.OS.Framework.Modules.Module;

namespace IntoChat.OS.Kernel;

// The one place the operating system's module composition is defined: discovered by reflection, not listed.
// Every host of the OS registers modules through it. A distribution is a diff against this
// composition, not a copy of it. The Workshop appends modules through Generated().
public static partial class OsCatalog
{
    public static IReadOnlyList<OsModule> Modules { get; } = [.. Discover(), .. Generated().Select(OsModule.From)];

    // The composition a test process can host by itself: every module that needs no container and loads under local.
    public static IReadOnlyList<OsModule> InProcessModules { get; } = [.. Modules.Where(module => !module.RequiresContainer && module.LoadsUnder("local"))];

    // Module types that are deliberately not in the portable composition: the Windows edge
    // (added by WithWindowsEdge) and the template sample.
    internal static bool IsExcluded(Type type)
        => type == typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule) || type.Name == "SampleModule";

    // Every public, concrete module with a public parameterless constructor in the module assemblies the
    // OS ships (the Kernel's reference closure and the module assemblies beside it), minus the exclusions,
    // ordered by order and then by id. A Framework module and a [Module] DigitalBrain module in a module
    // assembly are both catalog modules. A DigitalBrain type that an old module already names as Neurons is
    // that module's neurons, not a second catalog entry. Authority and confirmation modules are hosted by
    // the server directly; they are not catalog entries.
    private static OsModule[] Discover()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([typeof(OsCatalog).Assembly]);
        // A project reference the Kernel csproj keeps is copied beside the host even when no type in this
        // assembly names it, so the compiler omits it from GetReferencedAssemblies. Cloud modules are that case.
        foreach (var pattern in new[] { "IntoChat.OS.Modules.*.dll", "IntoChat.Cloud.*.dll" })
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, pattern))
            {
                try { queue.Enqueue(Assembly.Load(AssemblyName.GetAssemblyName(file))); }
                catch (BadImageFormatException) { continue; }
                catch (FileLoadException) { continue; }
            }
        }
        var legacy = new List<FrameworkModule>();
        var digital = new List<Type>();
        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.Add(assembly.FullName!)) { continue; }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { queue.Enqueue(Assembly.Load(reference)); }
                catch (FileNotFoundException) { continue; }
                catch (FileLoadException) { continue; }
            }
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null) { continue; }
                if (typeof(FrameworkModule).IsAssignableFrom(type)) { legacy.Add((FrameworkModule)Activator.CreateInstance(type)!); }
                else if (IsProductDigital(type)) { digital.Add(type); }
            }
        }
        var companions = legacy.Select(module => module.Neurons).OfType<Type>().ToHashSet();
        var modules = legacy.Where(module => !IsExcluded(module.GetType())).Select(OsModule.From).ToList();
        foreach (var type in digital)
        {
            if (companions.Contains(type) || IsExcluded(type) || modules.Any(module => module.Type == type)) { continue; }
            modules.Add(OsModule.FromDigital(type));
        }
        return [.. modules.OrderBy(module => module.Order).ThenBy(module => module.Id.Value, StringComparer.Ordinal)];
    }

    private static bool IsProductDigital(Type type)
    {
        if (!typeof(DigitalBrain.Module).IsAssignableFrom(type) || type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is null) { return false; }
        // Orleans is a catalog module, but it lives in the kernel composition assembly, not a module assembly.
        if (type == typeof(IntoChat.OS.Modules.Microsoft.Orleans.OrleansModule)) { return true; }
        var name = type.Assembly.GetName().Name ?? "";
        return name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) || name.StartsWith("IntoChat.Cloud.", StringComparison.Ordinal);
    }

    // Modules the Workshop has added to this OS. Declared here, implemented in
    // OsCatalog.Generated.cs so the composition grows without hand-editing this file.
    private static partial FrameworkModule[] Generated();

    // The Windows edge: the composition plus desktop automation of the Windows machine the host runs on.
    // Only a host on Windows opts in; the portable composition never carries it.
    public static IReadOnlyList<OsModule> WithWindowsEdge()
        => [.. Modules, OsModule.FromDigital(typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule))];

    // Composes the given modules (the portable composition by default) and the OS apps.
    public static IntoChatOSServerBuilder AddOperatingSystem(this IntoChatOSServerBuilder server, IReadOnlyList<OsModule>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var module in modules ?? Modules) { server.AddModule(module); }
        // The OS is the composition plus its apps; a distribution cannot forget them.
        server.ConfigureServices(services => services.AddOperatingSystemApps());
        return server;
    }
}
