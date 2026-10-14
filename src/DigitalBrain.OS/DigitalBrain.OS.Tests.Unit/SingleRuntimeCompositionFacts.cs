extern alias AppHost;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace DigitalBrain.OS.Tests.Unit;

[Collection(AppHostComposition.Collection)]
public sealed class SingleRuntimeCompositionFacts
{
    [Fact]
    public async Task DevicesDoNotRequireAnotherRuntimeOrATrustHost()
    {
        await using var host = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.DigitalBrain_AppHost>(
            ["Parameters:Modules-digitalbrain-master-key=synthetic-composition-test-key-32chars"], TestContext.Current.CancellationToken);
        var projects = host.Resources.OfType<ProjectResource>().ToArray();
        Assert.Single(projects, resource => resource.Name.StartsWith("OperatingSystem-", StringComparison.Ordinal));
        Assert.Single(projects, resource => resource.Name.StartsWith("digitalbrain-mcp-", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Resources, resource => resource.Name == "trust");
        foreach (var resource in projects.Where(resource => resource.Name is "OperatingSystem-a" or "digitalbrain-mcp-a"))
        {
            var environment = new Dictionary<string, object>();
            var context = new EnvironmentCallbackContext(host.ExecutionContext, resource, environment, TestContext.Current.CancellationToken);
            foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
            Assert.DoesNotContain("DigitalBrain__Platform__Trust__Endpoint", environment.Keys);
        }
    }
}

