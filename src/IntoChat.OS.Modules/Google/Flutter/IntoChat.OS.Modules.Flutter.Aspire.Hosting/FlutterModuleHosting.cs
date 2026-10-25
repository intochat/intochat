using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

public sealed class FlutterModuleHosting : ModuleHosting<IntoChat.OS.Modules.Flutter.FlutterModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        var options = brain.GetModuleConfiguration("flutter").GetModuleOptions<FlutterModuleOptions>("flutter").Hosting;
        var module = new IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting>(brain);
        switch (options.Kind)
        {
            case FlutterHostKind.None: break;
            case FlutterHostKind.Web: module.RunWebApp(Apply); break;
            case FlutterHostKind.Window: module.RunDesktopApp(Apply); break;
            default: throw new ArgumentOutOfRangeException(nameof(options));
        }
        void Apply(FlutterHostOptions target)
        {
            target.ResourceName = options.ResourceName;
            target.DeviceTarget = options.DeviceTarget;
            target.ShellName = options.ShellName;
            target.ChatName = options.ChatName;
            target.FlutterCommand = options.FlutterCommand;
            target.WorkingDirectory = options.WorkingDirectory;
            target.ReleaseBuild = options.ReleaseBuild;
            target.WebContainer = options.WebContainer;
        }
    }
}
