using DigitalBrain.AI.Tools.Web;
using DigitalBrain.AI.Tools.WebSearch;
using DigitalBrain.AI.Tools.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.AI.Tools;

internal static class NativeToolRegistrations
{
    public static void Add(IServiceCollection services, AIOptions options, AIWorkspaceOptions workspace)
    {
        services.TryAddSingleton<NativeTools>();
        services.TryAddSingleton<PlaywrightWebAgent>();
        services.AddNativeTool("browse_web", provider =>
            AIFunctionFactory.Create(provider.GetRequiredService<PlaywrightWebAgent>().ResearchAsync, "browse_web"));
        services.AddNativeTool("lookup_company", provider =>
            AIFunctionFactory.Create(provider.GetRequiredService<PlaywrightWebAgent>().LookupCompanyAsync, "lookup_company"));
        if (options.Tavily.Enabled)
        {
            services.AddNativeTool("websearch", provider => WebSearchFunction.Create(provider.GetRequiredService<IWebSearch>()));
        }

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            services.AddNativeTool("repositorydiff", provider => new RepositoryDiffFunction(provider.GetRequiredService<IOptions<AIWorkspaceOptions>>()).Function);
        }
    }
}
