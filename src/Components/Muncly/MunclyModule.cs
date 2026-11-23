using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Muncly;

[Module(ManifestName)]
public sealed class MunclyModule : Module
{
    internal const string ManifestName = "muncly";

    public const string BaseUrlField = "BaseUrl";
    public const string ToolSetsField = "ToolSets";
    public const string WebEmailField = "WebEmail";
    public const string WebPasswordField = "WebPassword";
    public const string McpTokenField = "McpToken";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [Setting.Of<MunclyOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts => typeof(MunclyOpen).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For(ManifestName, "Muncly CRM")
        .OffersSetting(BaseUrlField)
        .OffersSetting(ToolSetsField)
        .OffersSetting(WebEmailField)
        .OffersSecret(WebPasswordField)
        .OffersSecret(McpTokenField);

    public static ConnectorDefinition Connector => new(ManifestName, "Muncly CRM", ["crm"], ["muncly"])
    {
        Neurons = [],
        Signals = [typeof(MunclyOpen), typeof(MunclyConnect), typeof(MunclyReload)],
    };

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(new CompiledAppsRegistration(typeof(MunclyModule).Assembly));
    }
}
