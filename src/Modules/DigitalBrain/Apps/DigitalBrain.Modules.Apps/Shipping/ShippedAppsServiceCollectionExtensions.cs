using System.Reflection;
using DigitalBrain.Framework.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Apps;

public static class ShippedAppsServiceCollectionExtensions
{
    public static readonly string ConfigurationSection =
        DigitalBrain.DigitalBrainConfiguration.ModuleSection(new DigitalBrain.ModuleId("apps")) + ":Shipped";
    // The startup-shipping switch lives on the module's public surface so hosts and fixtures can
    // name it; ShippedAppPublisher (internal) is its reader.
    public static readonly string ShipOnStartupKey =
        DigitalBrain.DigitalBrainConfiguration.ModuleOptionsSection(new DigitalBrain.ModuleId("apps")) + ":ShipOnStartup";

    public static IServiceCollection AddShippedApps(this IServiceCollection services, Assembly assembly, string resourcePrefix, string publisher)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        return services.AddShippedAppSource(new EmbeddedShippedAppSource(assembly, resourcePrefix, publisher));
    }

    // Configuration names the vendors: DigitalBrain:Modules:apps:Shipped:{publisher}:Path points at a
    // directory of app folders. The deployment decides which vendors ship, not the host's code.
    public static IServiceCollection AddShippedApps(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var vendor in configuration.GetSection(ConfigurationSection).GetChildren())
        {
            var path = vendor["Path"];
            if (string.IsNullOrWhiteSpace(path))
            { throw new InvalidOperationException($"{ConfigurationSection}:{vendor.Key} names no Path."); }
            services.AddShippedAppSource(new DirectoryShippedAppSource(path, vendor.Key));
        }
        return services;
    }

    private static IServiceCollection AddShippedAppSource(this IServiceCollection services, IShippedAppSource source)
    {
        services.AddSingleton(source);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IShippedContentPublisher, ShippedAppPublisher>());
        return services;
    }
}
