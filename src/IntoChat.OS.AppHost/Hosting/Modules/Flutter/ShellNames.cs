using IntoChat.OS;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

public static class ShellNames
{
    // The module id is frozen in configuration; the dashboard shows it as "shell" (ResourceNames.Shown).
    public const string ModuleId = "flutter";
    // Run mode names the app after the component node. Publish keeps the name the deployed resource had.
    public const string DesktopResourceName = "shell-desktop";
    public const string WebResourceName = "shell-web";
    public const string LegacyResourceName = "FlutterShell";
    public const string UIBaseEnvironmentVariable = "INTOCHATOS_UI_BASE";
    public const string ShellEnvironmentVariable = "INTOCHATOS_SHELL";
    public const string ChatEnvironmentVariable = "INTOCHATOS_CHAT";
    public const string FlutterCommandEnvironmentVariable = "FLUTTER_COMMAND";
    public const string DefaultShellName = "desk";
    public const string DefaultChatName = "main";
    public const string DefaultDeviceTarget = "windows";
    // web-server is Flutter's headless web device: it serves the app over HTTP without driving
    // a browser of its own, so the fixed FlutterWebPort below is a real, addressable endpoint.
    // The "chrome" device never prints or exposes a served URL, which made RunWebApp
    // unreachable for automation.
    public const string DefaultWebDeviceTarget = "web-server";
    public const string WebPlatformDirectoryName = "web";
    public const string HttpEndpointName = "http";

    // Local-only DDS so AppHost can hot-reload without scraping a new URI each run.
    public const int FlutterDdsPort = 54721;
    public const int FlutterVmServicePort = 54722;
    // Fixed, unproxied serving port for the web host (--web-port), following the two ports
    // above: local-only, stable across runs, and registered as the flutter resource's real
    // Aspire HTTP endpoint so health checks and endpoint lookups work without log scraping.
    public const int FlutterWebPort = 54723;
    public const string FlutterWebHostname = "127.0.0.1";
}
