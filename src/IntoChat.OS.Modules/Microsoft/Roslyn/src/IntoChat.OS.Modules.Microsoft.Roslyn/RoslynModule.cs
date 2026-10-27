using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

public sealed class RoslynModule() : Module<RoslynModuleOptions>(new ModuleId("roslyn"))
{
    internal const string ManifestName = "roslyn";

    // R4-04: what the module needs from its host (RoslynNeurons).
    public static DigitalBrain.ModuleManifest Manifest => RoslynNeurons.Manifest;

    // The module's neurons are plain neurons (06-06a) on the runtime's host grains.
    public override Type? Neurons => typeof(RoslynNeurons);

    public override System.Reflection.Assembly? Contracts => typeof(IRoslyn).Assembly;

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RoslynModuleOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkspaceKey), "Roslyn workspace key must not be empty.")
            .ValidateOnStart();
        services.TryAddSingleton<SolutionWorkspace>();
        services.TryAddSingleton<ISolutionLoader, MSBuildSolutionLoader>();
        services.TryAddSingleton<CodeFixCatalog>();
        services.TryAddSingleton<ChangeSetEditor>();
        services.TryAddSingleton<SolutionFileWatcher>();
        services.AddHostedService<WorkspaceWarmup>();
    }
}

// The DigitalBrain module of Roslyn (06-06a): its assembly's [Neuron] classes run on the runtime's host grains, on TestOS and on
// the silo alike.
[DigitalBrain.Module(RoslynModule.ManifestName)]
public sealed class RoslynNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<RoslynModuleOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(RoslynModule.ManifestName))],
        Secrets: []);
}
