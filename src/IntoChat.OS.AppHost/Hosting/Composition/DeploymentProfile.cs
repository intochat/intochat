using Aspire.Hosting;
using IntoChat.OS.Host.Cluster;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Microsoft.Orleans;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// One deployment's runtime shape, resolved once from configuration: the topology (IntoChatOS:Topology, unset = local)
// picks the side (Device, Cloud, or both) and storage; module enablement (IntoChatOS:Modules:<id>:Enabled, defaulted by
// LocalRunDefaults) gates each module hosting in the AppHost chain. The kernel and MCP apply the same defaults themselves,
// so only explicit overrides are projected.
public sealed record DeploymentProfile
{
    public const string UiPortKey = "IntoChatOS:Http:UiPort";
    public const string McpPortKey = "IntoChatOS:Http:McpPort";
    public const string ConfigFileKey = "IntoChatOS:ConfigFile";

    public required string Topology { get; init; }
    public bool Cloud => Topology == IntoChatOSNames.TopologyCloud;
    public bool UsesDevice => Topology is IntoChatOSNames.TopologyLocal or IntoChatOSNames.TopologyLocalCloud;
    public bool UsesCloud => Topology is IntoChatOSNames.TopologyCloud or IntoChatOSNames.TopologyLocalCloud;
    public required string Home { get; init; }
    public int UiPort { get; init; } = 5080;
    public int McpPort { get; init; } = 5081;
    public bool ShellEnabled { get; init; } = true;
    public bool FrontEnabled { get; init; } = true;
    public required IReadOnlyList<string> ModuleOverrides { get; init; }
    public required OrleansOptions Orleans { get; init; }
    private IConfiguration Configuration { get; init; } = null!;

    public bool IsEnabled(string moduleId) => LocalRunDefaults.IsEnabled(Configuration, moduleId);

    // Loads IntoChatOS:ConfigFile and the topology's module defaults into the builder's configuration, then resolves the profile.
    public static DeploymentProfile From(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        if (configuration[ConfigFileKey] is { Length: > 0 } configFile) { configuration.AddJsonFile(configFile, optional: false); }
        var topology = IntoChatOSNames.CanonicalTopology(configuration[IntoChatOSNames.TopologyKey])
            ?? throw new InvalidOperationException(
                $"{IntoChatOSNames.TopologyKey} must be one of {IntoChatOSNames.TopologyLocal}, {IntoChatOSNames.TopologyLocalCloud} or {IntoChatOSNames.TopologyCloud}.");
        var overrides = ExplicitModuleSettings(configuration, topology);
        LocalRunDefaults.Apply(configuration);

        var cloud = topology == IntoChatOSNames.TopologyCloud;
        var orleans = new OrleansOptions();
        // Azurite carries grain state and the cookie key ring on cloud; a local run keeps both on the machine.
        if (cloud) { orleans.WithAzurite(); }
        var hasLocalFlutterUi = HasLocalFlutterPackage(builder.AppHostDirectory);
        return new()
        {
            Topology = topology,
            Home = IntoChatOSHome.Resolve(configuration),
            UiPort = configuration.GetValue(UiPortKey, 5080),
            McpPort = configuration.GetValue(McpPortKey, 5081),
            ShellEnabled = configuration.GetValue(IntoChatOSNames.ShellEnabledKey, hasLocalFlutterUi),
            FrontEnabled = configuration.GetValue(IntoChatOSNames.FrontEnabledKey, true),
            ModuleOverrides = overrides,
            Orleans = orleans,
            Configuration = configuration,
        };
    }

    private static bool HasLocalFlutterPackage(string appHostDirectory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(appHostDirectory)); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "ui", "pubspec.yaml")))
            {
                return true;
            }
        }
        return false;
    }

    // The Enabled settings the AppHost's own configuration states that differ from the topology default: the kernel and MCP
    // apply the default themselves, so these are all they need to hear.
    internal static IReadOnlyList<string> ExplicitModuleSettings(IConfiguration configuration, string topology)
    {
        var defaults = LocalRunDefaults.For(topology);
        return [.. configuration.GetSection(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModulesSectionName).GetChildren()
            .Select(module => LocalRunDefaults.EnabledKey(module.Key))
            .Where(key => configuration[key] is { Length: > 0 } value
                && !string.Equals(value, defaults.GetValueOrDefault(key) ?? "true", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }
}

// Where Aspire dev local state lives: IntoChatOS:Home, or ~/.intochat-aspire
// (isolated from IntoChat.OS.Desktop's ~/.intochat so their separate master keys and file locks never collide).
public static class IntoChatOSHome
{
    public static string Resolve(IConfiguration configuration) => DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration,
        new DigitalBrain.Runtime.Orleans.RuntimeOptions { HomeKey = IntoChatOSNames.HomeKey, DefaultHomeDirectoryName = ".intochat-aspire" }).Path;
}
