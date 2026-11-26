using System.Reflection;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Notifications;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class NotificationsModule : DigitalBrain.Modules.Module, IHttpModule
{
    internal const string ManifestName = "notifications";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [Setting.Of<NotificationsOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static Assembly? Contracts => typeof(INotificationsInbox).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints) => NotificationsEndpoints.Map(endpoints);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<NotificationsOptions>()
            .Validate(static options => options.MaxItems > 0, "Notifications MaxItems must be positive.")
            .ValidateOnStart();
    }
}
