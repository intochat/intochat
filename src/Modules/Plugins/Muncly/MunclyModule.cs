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

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [Setting.Of<MunclyOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts => typeof(MunclyOpen).Assembly;

    public static IntegrationSchema Integration { get; } = IntegrationSchema.For(ManifestName, "Muncly CRM")
        .OffersSetting(BaseUrlField)
        .OffersSetting(ToolSetsField);

    public static ConnectorDefinition Connector => new(ManifestName, "Muncly CRM", ["bearer-token"], [])
    {
        Neurons = [],
        Signals = [typeof(MunclyOpen), typeof(MunclyConnect), typeof(MunclyReload), typeof(MunclyProbe), typeof(MunclyDisconnect), typeof(MunclyToggleInspector)],
    };

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(new CompiledAppsRegistration(typeof(MunclyModule).Assembly));
    }
}
