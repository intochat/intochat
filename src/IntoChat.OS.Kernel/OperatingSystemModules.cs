using System.Reflection;
using DigitalBrain.Components;
using DigitalBrain.Modules;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel;

// The operating system's module list: every module type discovered at compile time across the kernel and its
// referenced module/component assemblies. External hosts can compose additional modules via RegisteredModuleAttribute
// or explicit module types without runtime GetExportedTypes / DependencyContext scanning.
public static partial class OperatingSystemModules
{
    public const string ModuleMetadataKey = "IntoChat.OS.Module";

    [GeneratedModuleRegistry]
    private static partial IReadOnlyList<Type> BuiltInModuleTypes();

    public static IReadOnlyList<OsModule> Modules { get; } = BuildCatalog(BuiltInModuleTypes());

    // The composition a test process can host by itself: every module that needs no container and loads under local.
    public static IReadOnlyList<OsModule> InProcessModules { get; } = [.. Modules.Where(module => !module.RequiresContainer && module.LoadsUnder("local"))];

    // Component assemblies (IntoChat.<Name>, excluding IntoChat.OS*) derived from the single compile-time module catalog.
    public static IReadOnlyList<Assembly> ComponentAssemblies { get; } =
    [
        .. Modules
            .Select(static module => module.Type.Assembly)
            .Distinct()
            .Where(IsComponentAssembly),
    ];

    public static bool DeclaresModule(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(entry => entry.Key == ModuleMetadataKey && string.Equals(entry.Value, "true", StringComparison.Ordinal));
    }

    // Module types that are deliberately not in the portable composition: the Windows edge
    // (added by WithWindowsEdge) and the template sample.
    internal static bool IsExcluded(Type type)
        => type == typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule) || type.Name == "SampleModule";

    public static IReadOnlyList<OsModule> Discover(Assembly? root = null)
    {
        var self = typeof(OperatingSystemModules).Assembly;
        if (root is null || root == self)
        {
            return Modules;
        }

        var extra = root.GetCustomAttributes<RegisteredModuleAttribute>()
            .Select(static attribute => attribute.ModuleType);
        return BuildCatalog(BuiltInModuleTypes().Concat(extra));
    }

    public static IReadOnlyList<OsModule> Discover(IEnumerable<Type> additionalModuleTypes)
    {
        ArgumentNullException.ThrowIfNull(additionalModuleTypes);
        return BuildCatalog(BuiltInModuleTypes().Concat(additionalModuleTypes));
    }

    private static IReadOnlyList<OsModule> BuildCatalog(IEnumerable<Type> candidateTypes)
    {
        var modules = new List<OsModule>();
        var seen = new HashSet<Type>();
        foreach (var type in candidateTypes)
        {
            if (!seen.Add(type) || type.IsAbstract || !IsProductModule(type) || IsExcluded(type))
            {
                continue;
            }

            modules.Add(OsModule.FromDigital(type));
        }

        var companions = modules.Where(module => module.Neurons is { } neurons && neurons != module.Type).Select(module => module.Neurons!).ToHashSet();
        modules.RemoveAll(module => companions.Contains(module.Type));
        return [.. modules.OrderBy(module => module.Order).ThenBy(module => module.Id.Value, StringComparer.Ordinal)];
    }

    private static bool IsComponentAssembly(Assembly assembly)
        => assembly.GetName().Name is { } name
            && name.StartsWith("IntoChat.", StringComparison.Ordinal)
            && !name.StartsWith("IntoChat.OS.", StringComparison.Ordinal)
            && !string.Equals(name, "IntoChat.OS", StringComparison.Ordinal);

    private static bool IsProductModule(Type type)
    {
        if (!typeof(DigitalBrain.Modules.Module).IsAssignableFrom(type) || type.GetCustomAttribute<ModuleAttribute>() is null) { return false; }
        // The kernel composition carries three product modules of its own: Orleans, the http@1 connector, and Identity.
        if (type == typeof(IntoChat.OS.Kernel.Composition.OrleansModule)) { return true; }
        if (type == typeof(IntoChat.OS.Kernel.Composition.Capabilities.HttpModule)) { return true; }
        if (type == typeof(IntoChat.OS.Modules.Identity.IdentityModule)) { return true; }
        return DeclaresModule(type.Assembly);
    }

    // The Windows edge: the composition plus desktop automation of the Windows machine the host runs on.
    // Only a host on Windows opts in; the portable composition never carries it.
    public static IReadOnlyList<OsModule> WithWindowsEdge(Assembly? root = null)
    {
        var baseModules = root is null ? Modules : Discover(root);
        var windows = OsModule.FromDigital(typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule));
        return baseModules.Any(module => module.Type == windows.Type) ? baseModules : [.. baseModules, windows];
    }

    // P6 privileged modules exempt from same-brain slot binding audit warnings while carrying host/silo plumbing.
    private static readonly HashSet<string> PrivilegedBindingComponents = new(StringComparer.Ordinal)
    {
        "IntoChat.OS.Modules.AI",
        "IntoChat.OS.Modules.Identity",
        "IntoChat.OS.Modules.Infrastructure",
        "IntoChat.OS.Modules.Plugins",
        "IntoChat.OS.Modules.Roslyn",
        "IntoChat.OS.Modules.Workspace",
    };

    // Composes the given modules (the portable composition by default) and the OS apps.
    public static IntoChatOSServerBuilder AddOperatingSystem(this IntoChatOSServerBuilder server, IReadOnlyList<OsModule>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var selected = modules ?? Modules;
        foreach (var module in selected) { server.AddModule(module); }
        server.AddNeuronModule(typeof(IntoChat.OS.Apps.ShippedShellAppsModule));
        server.ConfigureServices(services =>
        {
            services.AddSingleton<IntoChat.OS.Modules.Microsoft.Azure.IPulumiEngine>(new Install.KernelPulumiEngine(selected));
            services.AddMeteredHttpPipelines(selected);
            services.AddOperatingSystemApps();
            services.TryAddSingleton(sp =>
            {
                var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IntoChat.OS.Kernel.BindingGate");
                return new BindingGateOptions
                {
                    Mode = BindingEnforcementMode.Audit,
                    PrivilegedComponents = PrivilegedBindingComponents,
                    OnMissingBinding = logger is null
                        ? null
                        : ev => logger.LogWarning(
                            "binding.missing: component={Component} target={Target} op={Operation}",
                            ev.Component.Value,
                            ev.Target,
                            ev.Operation),
                };
            });
        });
        return server;
    }
}
