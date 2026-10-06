using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace DigitalBrain.Aspire.Testing;

// Composition facts run against the application model alone: build the graph, then read the
// annotations and the environment a resource would receive, without starting anything.
public static class AspireCompositionTest
{
    public static IDistributedApplicationBuilder CreateBuilder()
        => DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });

    public static IDistributedApplicationBuilder CreatePublishBuilder()
        => DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--publisher", "manifest"], DisableDashboard = true });

    public static async Task<Dictionary<string, object>> EnvironmentOf(
        IDistributedApplicationBuilder builder, IResource resource, CancellationToken cancellationToken = default)
    {
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, environment, cancellationToken);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        return environment;
    }
}
