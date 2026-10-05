using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting;

public static class HostingModuleOptions
{
    public static T GetModuleOptions<T>(this IConfiguration configuration, string id) where T : class, IModuleOptions, new()
    {
        var value = configuration.GetSection(DigitalBrainConfiguration.ModuleOptionsSection(id)).Get<T>() ?? new();
        value.Validate();
        return value;
    }
    internal static Dictionary<string, string?> Flatten<T>(T options, string id) where T : notnull, new()
    {
        // Only values the deployment actually decided become configuration; defaults stay the
        // receiver's own to override.
        return DigitalBrainConfiguration.Flatten(options, DigitalBrainConfiguration.ModuleOptionsSection(id), omitDefaults: true);
    }
}
