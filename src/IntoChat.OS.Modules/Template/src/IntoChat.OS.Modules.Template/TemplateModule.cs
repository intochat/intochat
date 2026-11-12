using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Modules.Template.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Template;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class TemplateModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "template";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<TemplateOptions>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts => typeof(ITemplateNeuron).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TemplateOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.DefaultPrefix), "Template default prefix must not be empty.")
            .ValidateOnStart();
    }
}
