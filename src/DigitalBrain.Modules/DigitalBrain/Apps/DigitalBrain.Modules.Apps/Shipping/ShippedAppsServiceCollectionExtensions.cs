using System.Reflection;
using DigitalBrain.Framework.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

public static class ShippedAppsServiceCollectionExtensions
{
    public static readonly string ConfigurationSection =
        DigitalBrain.Framework.DigitalBrainConfiguration.ModuleSection(new DigitalBrain.Framework.ModuleId("apps")) + ":Shipped";
    // The startup-shipping switch lives on the module's public surface so hosts and fixtures can
    // name it; ShippedAppPublisher (internal) is its reader.
    public static readonly string ShipOnStartupKey =
        DigitalBrain.Framework.DigitalBrainConfiguration.ModuleOptionsSection(new DigitalBrain.Framework.ModuleId("apps")) + ":ShipOnStartup";

    public static IServiceCollection AddShippedApps(this IServiceCollection services, Assembly assembly, string resourcePrefix, string account)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return services.AddShippedAppSource(new EmbeddedShippedAppSource(assembly, resourcePrefix, account));
    }

    // Configuration names the sources: DigitalBrain:Modules:apps:Shipped:{source}:Path points at a
    // directory of app folders and :Account names the publisher account that owns them; :Writable
    // lets publishing write back into the folder (development). The deployment decides, not the code.
    public static IServiceCollection AddShippedApps(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var source in configuration.GetSection(ConfigurationSection).GetChildren())
        {
            var path = source["Path"];
            if (string.IsNullOrWhiteSpace(path))
            { throw new InvalidOperationException($"{ConfigurationSection}:{source.Key} names no Path."); }
            var account = source["Account"];
            if (string.IsNullOrWhiteSpace(account))
            { throw new InvalidOperationException($"{ConfigurationSection}:{source.Key} names no Account."); }
            var writable = bool.TryParse(source["Writable"], out var flag) && flag;
            services.AddShippedAppSource(new DirectoryShippedAppSource(path, account, writable));
        }
        return services;
    }

    private static IServiceCollection AddShippedAppSource(this IServiceCollection services, IShippedAppSource source)
    {
        services.AddSingleton(source);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IShippedContentPublisher, ShippedAppPublisher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ShippedAppWriteBack>());
        return services;
    }
}
