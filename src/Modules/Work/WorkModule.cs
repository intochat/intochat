using DigitalBrain.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Work;

[Module(ModuleName)]
public sealed class WorkModule : Module
{
    internal const string ModuleName = "work";

    public static System.Reflection.Assembly? Contracts => typeof(WorkModule).Assembly;

    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<WorkApprovalTracker>();
        services.TryAddSingleton<WorkOrchestratorNeuron>();
        services.TryAddSingleton<IWorkOrchestrator>(sp => sp.GetRequiredService<WorkOrchestratorNeuron>());
    }
}
