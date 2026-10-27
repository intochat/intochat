using IntoChat.OS;
using IntoChat.OS.Modules.Flutter.Space;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Flutter;

public sealed class FlutterModule() : Module<FlutterModuleOptions>(new ModuleId(ModuleName)), IHttpModule
{
    internal const string ModuleName = "flutter";

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Flutter.Button.IButton).Assembly;

    // R4-04: what the module needs from its host (FlutterNeurons). Configure keeps the registrations of the neurons that are
    // still grains (Chat, Composer, Space; 06-05) until Configure leaves with the Framework (06-08).
    public static DigitalBrain.ModuleManifest Manifest => FlutterNeurons.Manifest;

    // The migrated controls (06-04) run as plain neurons on the runtime's host grains.
    public override Type? Neurons => typeof(FlutterNeurons);

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<CircleMembers>();
        services.AddHostedService<SpaceWatcher>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        WorkspaceEndpoints.Map(endpoints);
        LiveSignalEndpoints.Map(endpoints);
        BrainEventsEndpoints.Map(endpoints);
        AppUiEndpoints.Map(endpoints);
        Settings.NotificationPreferenceEndpoints.Map(endpoints);
        Settings.WorkspacePreferenceEndpoints.Map(endpoints);
        Settings.ChatEndpoints.Map(endpoints);
    }
}

// The DigitalBrain module of Flutter (06-04): its assembly's [Neuron] classes (the migrated controls) run on the runtime's host
// grains, on TestOS and on the silo alike.
[DigitalBrain.Module(FlutterModule.ModuleName)]
public sealed class FlutterNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<FlutterModuleOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId(FlutterModule.ModuleName)))],
        Secrets: []);
}
