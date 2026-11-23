using DigitalBrain.Modules.Options;
using IntoChat.OS;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Flutter;

// Preserve serialized values; 1 belonged to the removed Dart console host.
public enum FlutterHostKind { Window = 0, Web = 2, None = 3 }
public enum AndroidDebugTransport { Wireless = 0, Usb = 1, Auto = 2 }
public sealed record FlutterHostingOptions
{
    public FlutterHostKind Kind { get; init; } = FlutterHostKind.Window;
    public string? ResourceName { get; init; }
    public string DeviceTarget { get; init; } = "windows";
    public string ShellName { get; init; } = "desk";
    public string ChatName { get; init; } = "main";
    public string? FlutterCommand { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool ReleaseBuild { get; init; }
    // Web only: serve the prebuilt release bundle from a docker image instead of compiling per AppHost.
    // Describes the machine running the AppHost, so CI flips it for every host via configuration.
    public bool WebContainer { get; init; }
    // Android client target: selects wireless (ADB Wi-Fi / mDNS), USB, or auto-detect.
    public AndroidDebugTransport AndroidTransport { get; init; } = AndroidDebugTransport.Wireless;
    // Optional explicit ADB serial or host:port override for the Android client target.
    public string? AndroidDevice { get; init; }
}
public sealed record FlutterModuleOptions : IModuleOptions
{
    [Setting] public FlutterHostingOptions Hosting { get; set; } = new();

    public FlutterModuleOptions RunWebApp() => WithHost(FlutterHostKind.Web);
    public FlutterModuleOptions RunDesktopApp() => WithHost(FlutterHostKind.Window);
    public FlutterModuleOptions BackendOnly() => WithHost(FlutterHostKind.None);

    public void Validate()
    {
        if (!Enum.IsDefined(Hosting.Kind)) { throw new ArgumentOutOfRangeException(nameof(Hosting), "Unknown Flutter host kind."); }
        if (!Enum.IsDefined(Hosting.AndroidTransport)) { throw new ArgumentOutOfRangeException(nameof(Hosting), "Unknown Android debug transport."); }
        if ((Hosting.ResourceName is not null && string.IsNullOrWhiteSpace(Hosting.ResourceName)) || string.IsNullOrWhiteSpace(Hosting.ShellName)
            || string.IsNullOrWhiteSpace(Hosting.ChatName))
        { throw new ArgumentException("Flutter resource, shell and chat names must be specified."); }
    }

    private FlutterModuleOptions WithHost(FlutterHostKind kind) { Hosting = Hosting with { Kind = kind }; return this; }
}
