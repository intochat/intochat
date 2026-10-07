using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Apps;

public static class ShippedAppsServiceCollectionExtensions
{
    public static readonly string ConfigurationSection =
        DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.ModuleSection(new DigitalBrain.Framework.Modules.ModuleId("apps")) + ":Shipped";

    public static IServiceCollection AddShippedApps(this IServiceCollection services, Assembly assembly, string resourcePrefix, string publisher)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        return services.AddShippedAppSource(new EmbeddedShippedAppSource(assembly, resourcePrefix, publisher));
    }

    // Configuration names the sources: DigitalBrain:Modules:apps:Shipped:{vendor}:Path points at a
    // directory of app folders the catalog lists under that vendor. The deployment decides, not the code;
    // a development host registers the folders writable for Save to source.
    public static IServiceCollection AddShippedApps(this IServiceCollection services, IConfiguration configuration, bool development = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var source in configuration.GetSection(ConfigurationSection).GetChildren())
        {
            var path = source["Path"];
            if (string.IsNullOrWhiteSpace(path))
            { throw new InvalidOperationException($"{ConfigurationSection}:{source.Key} names no Path."); }
            services.AddShippedAppSource(new DirectoryShippedAppSource(path, source.Key, development));
        }
        return services;
    }

    private static IServiceCollection AddShippedAppSource(this IServiceCollection services, IShippedAppSource source)
    {
        services.AddSingleton(source);
        services.TryAddSingleton<ShippedCatalog>();
        return services;
    }
}
