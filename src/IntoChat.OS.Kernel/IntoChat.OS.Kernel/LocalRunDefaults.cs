using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Kernel;

// A local kernel run has no containers. These are the modules the AppHost local profile switches off
// (IntoChatOSNames.LocalDisabledModules). Lowest precedence, so an explicit Enabled still wins. Cloud does not apply them.
internal static class LocalRunDefaults
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InitModuleConfiguration()
        => ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };

    public static void Apply(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
        var topology = configuration[IntoChatOSNames.TopologyKey];
        if (topology is { Length: > 0 }
            && !string.Equals(topology, IntoChatOSNames.TopologyLocal, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(topology, IntoChatOSNames.TopologyLocalCloud, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(topology, IntoChatOSNames.TopologyLocalCloudAlias, StringComparison.OrdinalIgnoreCase))
        { return; }
        configuration.Sources.Insert(0, new MemoryConfigurationSource
        {
            InitialData = IntoChatOSNames.LocalDisabledModules.ToDictionary(
                static id => ModuleConfiguration.ModuleSection(id) + ":Enabled",
                static _ => (string?)"false",
                StringComparer.OrdinalIgnoreCase),
        });
    }
}
