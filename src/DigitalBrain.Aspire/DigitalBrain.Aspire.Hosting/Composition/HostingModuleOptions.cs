using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.Configuration;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Aspire.Hosting;

public static class HostingModuleOptions
{
    internal static Dictionary<string, string?> Flatten<T>(T options, string id) where T : notnull, new()
    {
        // Only values the deployment actually decided become configuration; defaults stay the
        // receiver's own to override.
        return DigitalBrainConfiguration.Flatten(options, DigitalBrainConfiguration.ModuleOptionsSection(id), omitDefaults: true);
    }
}
