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
    public override System.Reflection.Assembly? Contracts => typeof(IRoslyn).Assembly;

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RoslynModuleOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkspaceKey), "Roslyn workspace key must not be empty.")
            .ValidateOnStart();
        services.TryAddSingleton<SolutionWorkspace>();
        services.TryAddSingleton<ISolutionLoader, MSBuildSolutionLoader>();
        services.TryAddSingleton<CodeFixCatalog>();
        services.TryAddSingleton<ChangeSetEditor>();
        services.TryAddSingleton<SolutionFileWatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<RoslynNeuron>, RoslynActivation>());
        services.AddHostedService<WorkspaceWarmup>();
    }
}
