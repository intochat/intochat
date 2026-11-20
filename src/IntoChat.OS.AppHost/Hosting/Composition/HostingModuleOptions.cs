using System.Text.Json;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

public static class HostingModuleOptions
{
    static HostingModuleOptions() => ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
    internal static Dictionary<string, string?> Flatten<T>(T options, string id) where T : notnull, new()
    {
        // Only values the deployment actually decided become configuration; defaults stay the
        // receiver's own to override.
        return ModuleConfiguration.Flatten(options, ModuleConfiguration.ModuleOptionsSection(id), omitDefaults: true);
    }
}
