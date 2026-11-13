using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace IntoChat.OS.Framework.Configuration;

// The module defaults of a topology: local and local+cloud have no containers, so IntoChatOSNames.LocalDisabledModules
// default to off; cloud keeps every module on. Every process that reads module configuration (kernel, MCP, AppHost) applies
// them itself, at the lowest precedence, so an explicit IntoChatOS:Modules:<id>:Enabled from any later source still wins.
public static class LocalRunDefaults
{
    public static IReadOnlyDictionary<string, string?> For(string? topology)
        => IntoChatOSNames.UsesLocalModuleDefaults(topology)
            ? IntoChatOSNames.LocalDisabledModules.ToDictionary(EnabledKey, static _ => (string?)"false", StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    // Reads the topology from the configuration as composed so far; call it after every source that may set it.
    public static void Apply(IConfigurationBuilder configuration, IConfiguration composed)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(composed);
        configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = For(composed[IntoChatOSNames.TopologyKey]) });
    }

    public static void Apply(ConfigurationManager configuration) => Apply(configuration, configuration);

    public static string EnabledKey(string moduleId) => ModuleConfiguration.ModuleSection(moduleId) + ":Enabled";

    // Whether a module runs under this configuration (defaults applied): unset means on.
    public static bool IsEnabled(IConfiguration configuration, string moduleId)
        => configuration.GetValue<bool?>(EnabledKey(moduleId)) ?? true;
}
