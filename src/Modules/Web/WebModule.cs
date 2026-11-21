using System.Reflection;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Web;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class WebModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "webtools";
    internal const string PublicHttpClientName = "IntoChat.OS.Modules.Web.Public";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.HttpClient(ManifestName, requestsPerMinute: 60)],
        Settings: [Setting.Of<WebModuleOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static Assembly? Contracts => typeof(IWebSearch).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddHttpClient(PublicHttpClientName, static client =>
        {
            client.Timeout = HostBoundHttp.Timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IntoChatOS-WebRead/1.0");
        }).ConfigurePrimaryHttpMessageHandler(static () => HostBoundHttp.Handler(VettedConnect.Dns));
    }
}
