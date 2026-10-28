using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace IntoChat.OS.Kernel;

// A local kernel run has no containers. These are the modules the AppHost local profile switches off
// (RunProfile.LocalDisabledModules). Lowest precedence, so an explicit Enabled still wins. Cloud does not apply them.
internal static class LocalRunDefaults
{
    private static readonly string[] Disabled =
        ["postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "github", "azure", "roslyn", "salesforce", "gmail"];

    public static void Apply(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var topology = configuration[IntoChatOSNames.TopologyKey];
        if (topology is { Length: > 0 }
            && !string.Equals(topology, IntoChatOSNames.TopologyLocal, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(topology, IntoChatOSNames.TopologyLocalCloud, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(topology, IntoChatOSNames.TopologyLocalCloudAlias, StringComparison.OrdinalIgnoreCase))
        { return; }
        configuration.Sources.Insert(0, new MemoryConfigurationSource
        {
            InitialData = Disabled.ToDictionary(
                static id => IntoChatOSConfiguration.ModuleSection(id) + ":Enabled",
                static _ => (string?)"false",
                StringComparer.OrdinalIgnoreCase),
        });
    }
}
