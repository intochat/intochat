using System.Reflection;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;

namespace IntoChat.OS.Modules.Web;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class WebModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "webtools";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.HttpClient(ManifestName, requestsPerMinute: 60)],
        Settings: [Setting.Of<WebModuleOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static Assembly? Contracts => typeof(IWebSearch).Assembly;
}
