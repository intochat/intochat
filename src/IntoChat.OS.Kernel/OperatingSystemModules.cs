using System.Reflection;
using DigitalBrain.Components;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyModel;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Kernel;

// The operating system's module list: every module assembly reachable from the host root (and the kernel itself).
// Hosts may pass their own root assembly to compose extra module assemblies on top of the kernel's catalog.
public static class OperatingSystemModules
{
    public const string ModuleMetadataKey = "IntoChat.OS.Module";

    // Assemblies that carry product modules without the IntoChat.OS.Module metadata: the kernel itself and Identity.
    private static readonly Assembly[] KernelOwnedAssemblies =
    [
        typeof(OperatingSystemModules).Assembly,
        typeof(IntoChat.OS.Modules.Identity.IdentityModule).Assembly,
    ];

    private static readonly Assembly[] BuiltInModuleAssemblies =
    [
        .. KernelOwnedAssemblies,
        typeof(IntoChat.OS.Modules.AI.AIModule).Assembly,
        typeof(IntoChat.OS.Modules.Runs.RunsModule).Assembly,
        typeof(IntoChat.OS.Modules.Authoring.AuthoringModule).Assembly,
        typeof(IntoChat.OS.Modules.ArtCraft.ArtCraftModule).Assembly,
        typeof(IntoChat.OS.Modules.Microsoft.Azure.AzureModule).Assembly,
        typeof(IntoChat.Aspire.AspireModule).Assembly,
        typeof(IntoChat.Muncly.MunclyModule).Assembly,
        typeof(IntoChat.OS.Modules.Apps.AppsModule).Assembly,
        typeof(IntoChat.OS.Modules.Circles.CirclesModule).Assembly,
        typeof(IntoChat.OS.Modules.ClickHouse.ClickHouseModule).Assembly,
        typeof(IntoChat.OS.Modules.Devices.DevicesModule).Assembly,
        typeof(IntoChat.OS.Modules.Microsoft.GitHub.GitHubModule).Assembly,
        typeof(IntoChat.OS.Modules.Google.Auth.GoogleAuthModule).Assembly,
        typeof(IntoChat.OS.Modules.Mcp.McpModule).Assembly,
        typeof(IntoChat.OS.Modules.Notifications.NotificationsModule).Assembly,
        typeof(IntoChat.OS.Modules.Microsoft.Playwright.PlaywrightModule).Assembly,
        typeof(IntoChat.OS.Modules.Postgres.PostgresModule).Assembly,
#if !INTOCHAT_OS_AOT
        typeof(IntoChat.OS.Modules.Microsoft.Roslyn.RoslynModule).Assembly,
#endif
        typeof(IntoChat.OS.Modules.Salesforce.SalesforceModule).Assembly,
        typeof(IntoChat.OS.Modules.Flutter.FlutterModule).Assembly,
        typeof(IntoChat.OS.Modules.LocalDisk.LocalDiskModule).Assembly,
        typeof(IntoChat.OS.Modules.Time.TimeModule).Assembly,
        typeof(IntoChat.OS.Modules.Web.WebModule).Assembly,
        typeof(IntoChat.OS.Modules.Microsoft.Windows.WindowsModule).Assembly,
    ];

    public static IReadOnlyList<OsModule> Modules { get; } = Discover(typeof(OperatingSystemModules).Assembly);

    // The composition a test process can host by itself: every module that needs no container and loads under local.
    public static IReadOnlyList<OsModule> InProcessModules { get; } = [.. Modules.Where(module => !module.RequiresContainer && module.LoadsUnder("local"))];

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
        var modules = new List<OsModule>();
        foreach (var assembly in CandidateAssemblies(root))
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
        // The kernel composition carries three product modules of its own: Orleans, the http@1 connector, and Identity.
        if (type == typeof(IntoChat.OS.Kernel.Composition.OrleansModule)) { return true; }
        if (type == typeof(IntoChat.OS.Kernel.Composition.Capabilities.HttpModule)) { return true; }
        if (type == typeof(IntoChat.OS.Modules.Identity.IdentityModule)) { return true; }
        return DeclaresModule(type.Assembly);
    }

    // Assemblies named by the Kernel + host root dependency closure, not a directory listing of whatever sits beside the process.
    private static IEnumerable<Assembly> CandidateAssemblies(Assembly? root = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Assembly>();
        var queue = new Queue<Assembly>();

        void Track(Assembly assembly)
        {
            var assemblyName = assembly.GetName().Name;
            if (assemblyName is null || !seen.Add(assemblyName)) { return; }
            if (DeclaresModule(assembly) || KernelOwnedAssemblies.Contains(assembly))
            {
                found.Add(assembly);
            }
            queue.Enqueue(assembly);
        }

        void Consider(AssemblyName name)
        {
            if (name.Name is null || ShouldSkipAssembly(name.Name) || seen.Contains(name.Name)) { return; }
            try
            {
                var loaded = Assembly.Load(name);
                Track(loaded);
            }
            catch (FileNotFoundException) { return; }
            catch (FileLoadException) { return; }
            catch (BadImageFormatException) { return; }
        }

        var self = typeof(OperatingSystemModules).Assembly;
        var rootAssembly = root ?? self;
        Track(self);
        if (rootAssembly != self)
        {
            Track(rootAssembly);
        }
        foreach (var builtIn in BuiltInModuleAssemblies)
        {
            Track(builtIn);
        }

        Assembly[] origins = rootAssembly == self ? [self] : [self, rootAssembly];
        foreach (var origin in origins)
        {
            if (DependencyContext.Load(origin) is not { } context) { continue; }
            foreach (var library in context.RuntimeLibraries) { Consider(new AssemblyName(library.Name)); }
        }

        while (queue.TryDequeue(out var assembly))
        {
            AssemblyName[] references;
            try { references = assembly.GetReferencedAssemblies(); }
            catch (PlatformNotSupportedException) { continue; }
            foreach (var reference in references) { Consider(reference); }
        }
        return found;
    }

    private static bool ShouldSkipAssembly(string name)
        => name is "netstandard" or "mscorlib" or "System" or "Orleans"
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name.StartsWith("Orleans.", StringComparison.Ordinal)
            || name.StartsWith("Aspire.", StringComparison.Ordinal)
            || name.StartsWith("DigitalBrain", StringComparison.Ordinal);

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
        "IntoChat.OS.Modules.Catalog",
        "IntoChat.OS.Modules.Devices",
        "IntoChat.OS.Modules.GitHub",
        "IntoChat.OS.Modules.Google",
        "IntoChat.OS.Modules.Identity",
        "IntoChat.OS.Modules.Mcp",
        "IntoChat.OS.Modules.Postgres",
        "IntoChat.OS.Modules.Roslyn",
        "IntoChat.OS.Modules.Storage",
        "IntoChat.OS.Modules.Windows",
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
