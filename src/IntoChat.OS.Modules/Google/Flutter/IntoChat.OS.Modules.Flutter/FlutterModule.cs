using IntoChat.OS;
using IntoChat.OS.Flutter.Space;
using IntoChat.OS.Flutter.Workspace;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Flutter;

public sealed class FlutterModule() : Module<FlutterModuleOptions>(new ModuleId("flutter")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Flutter.Button.IButton).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddSingleton<CircleMembers>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<Chat.ChatNeuron>, Chat.ChatActivation>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<Chat.ComposerNeuron>, Chat.ComposerActivation>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<SpaceNeuron>, SpaceActivation>());
        silo.Services.AddHostedService<SpaceWatcher>();
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
