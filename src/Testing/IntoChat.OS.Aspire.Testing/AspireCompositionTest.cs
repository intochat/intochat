using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Testing;

// Composition facts run against the application model alone: build the graph, then read the
// annotations and the environment a resource would receive, without starting anything.
public static class AspireCompositionTest
{
    public static IDistributedApplicationBuilder CreateBuilder()
        => DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });

    public static IDistributedApplicationBuilder CreatePublishBuilder()
        => DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--publisher", "manifest"], DisableDashboard = true });

    // Inspect the configuration consumed by .NET after environment-provider normalization.
    // Non-configuration variables (for example SYSTEMONE_MODEL) retain their original names.
    public static async Task<Dictionary<string, object>> ConfigurationOf(
        IDistributedApplicationBuilder builder, IResource resource, CancellationToken cancellationToken = default)
        => (await EnvironmentOf(builder, resource, cancellationToken))
            .ToDictionary(pair => IntoChatOSConfiguration.ConfigurationName(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);

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
