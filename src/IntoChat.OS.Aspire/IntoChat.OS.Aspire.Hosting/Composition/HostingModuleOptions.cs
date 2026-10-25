using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Modules;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

public static class HostingModuleOptions
{
    internal static Dictionary<string, string?> Flatten<T>(T options, string id) where T : notnull, new()
    {
        // Only values the deployment actually decided become configuration; defaults stay the
        // receiver's own to override.
        return IntoChatOSConfiguration.Flatten(options, IntoChatOSConfiguration.ModuleOptionsSection(id), omitDefaults: true);
    }
}
