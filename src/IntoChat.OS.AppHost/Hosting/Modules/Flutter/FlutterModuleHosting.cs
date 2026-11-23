using DigitalBrain.Modules.Options;
using IntoChat.OS.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

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
            target.AndroidTransport = options.AndroidTransport;
            target.AndroidDevice = options.AndroidDevice;
        }
    }
}
