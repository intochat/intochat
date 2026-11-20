using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Flutter;

[DigitalBrain.Modules.Module(ModuleName)]
public sealed class FlutterModule : DigitalBrain.Modules.Module, IHttpModule
{
    internal const string ModuleName = "flutter";

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly;

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<FlutterModuleOptions>(ModuleConfiguration.ModuleOptionsSection(new ModuleId(ModuleName)))],
        Secrets: []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        WorkspaceEndpoints.Map(endpoints);
        Screens.ScreensEndpoints.Map(endpoints);
        LiveSignalEndpoints.Map(endpoints);
        BrainEventsEndpoints.Map(endpoints);
        Workspace.UnitReadEndpoints.Map(endpoints);
        AppUiEndpoints.Map(endpoints);
        Settings.NotificationPreferenceEndpoints.Map(endpoints);
        Settings.WorkspacePreferenceEndpoints.Map(endpoints);
        Settings.ChatEndpoints.Map(endpoints);
    }
}
