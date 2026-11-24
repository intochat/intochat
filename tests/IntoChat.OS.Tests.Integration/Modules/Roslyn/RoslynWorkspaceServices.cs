using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Tests.Integration.Modules.Roslyn;

// The workspace services RoslynModule registers, without a solution to open or a warmup to run.
internal static class RoslynWorkspaceServices
{
    public static IServiceCollection AddRoslynWorkspace(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddOptions<RoslynModuleOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SolutionWorkspace>();
        services.TryAddSingleton<CodeGraphService>();
        services.TryAddSingleton<ISolutionLoader, MSBuildSolutionLoader>();
        services.TryAddSingleton<CodeFixCatalog>();
        services.TryAddSingleton<ChangeSetEditor>();
        return services;
    }
}
