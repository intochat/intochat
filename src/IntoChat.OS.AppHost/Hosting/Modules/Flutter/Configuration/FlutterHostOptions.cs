using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

public sealed class FlutterHostOptions
{
    public static readonly string SectionName =
        DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(new DigitalBrain.Modules.Options.ModuleId("flutter")) + ":Hosting";
    public static readonly string WebContainerKey = SectionName + ":WebContainer";

    // Overlays the module options (IntoChatOS:Modules:flutter:Options:Hosting) onto host options.
    public static FlutterHostOptions Overlay(FlutterHostOptions options, MicrosoftSdk.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        var hosting = DigitalBrain.Modules.Options.ModuleOptionsSerialization
            .GetModuleOptions<IntoChat.OS.Modules.Flutter.FlutterModuleOptions>(configuration, "flutter").Hosting;
        options.ResourceName = hosting.ResourceName;
        options.ShellName = hosting.ShellName;
        options.ChatName = hosting.ChatName;
        options.FlutterCommand = hosting.FlutterCommand ?? options.FlutterCommand;
        options.WorkingDirectory = hosting.WorkingDirectory ?? options.WorkingDirectory;
        options.ReleaseBuild = hosting.ReleaseBuild;
        options.WebContainer = hosting.WebContainer;
        // The record's default device target is the desktop one; keep the web default unless configured away from it.
        if (hosting.DeviceTarget != ShellNames.DefaultDeviceTarget) { options.DeviceTarget = hosting.DeviceTarget; }
        return options;
    }

    public FlutterHostKind Kind { get; set; } = FlutterHostKind.Window;

    // Null: the host names the app (shell-desktop or shell-web in run mode, FlutterShell in publish).
    public string? ResourceName { get; set; }

    public string DeviceTarget { get; set; } = ShellNames.DefaultDeviceTarget;

    public string ShellName { get; set; } = ShellNames.DefaultShellName;

    public string ChatName { get; set; } = ShellNames.DefaultChatName;

    public string? FlutterCommand { get; set; }

    public string? WorkingDirectory { get; set; }

    public bool ReleaseBuild { get; set; }

    // Web only: serve the prebuilt release bundle from a docker image (shell/Dockerfile)
    // instead of compiling per AppHost with flutter run. Hosting-layer knob, deliberately
    // absent from FlutterHostingOptions: it describes the machine running the AppHost, not
    // the product composition, so CI flips it for every host via configuration.
    public bool WebContainer { get; set; }
}
