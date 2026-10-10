extern alias AppHost;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.OS.Tests.Unit;

// Builds the real AppHost model without starting any resource. The CSharp module owns
// compilation and authoring; the product uses the same connector set as the reference composition.
[Collection(AppHostComposition.Collection)]
public sealed class ProductCompositionFacts
{
    [Fact]
    public async Task ClickHouseHostingUsesTheConfiguredDatabaseName()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.DigitalBrain_AppHost>(
            ["Parameters:Modules-digitalbrain-master-key=synthetic-composition-test-key-32chars",
             DigitalBrainConfiguration.ModuleOptionsSection("clickhouse") + ":DatabaseName=analytics"], TestContext.Current.CancellationToken);
        var database = Assert.Single(appHost.Resources.OfType<ClickHouseDatabaseResource>());
        Assert.Equal("analytics", database.DatabaseName);
    }

    // The hosting assemblies all sit in this test's output (via the AppHost reference), so a
    // hosting adapter referenced here but claiming an id outside the catalog is stale, and a
    // catalog module with an adapter in scope must resolve through the same scan the host uses.
    [Fact]
    public void EveryReferencedHostingAdapterBelongsToACatalogModule()
    {
        var adapters = ModuleHostingScan.Resolve(typeof(ProductCompositionFacts).Assembly);
        var catalogIds = DigitalBrain.OS.OsCatalog.Modules.Select(module => module.Id).ToHashSet();
        Assert.All(adapters.Keys, id => Assert.Contains(id, catalogIds));
        Assert.Contains(new ModuleId("postgres"), adapters.Keys);
        Assert.Contains(new ModuleId("qdrant"), adapters.Keys);
    }

    [Fact]
    public async Task TheAppHostRunsAnAlwaysOnSandboxAndProjectsItsUrlIntoTheBrain()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.DigitalBrain_AppHost>(
            ["Parameters:Modules-digitalbrain-master-key=synthetic-composition-test-key-32chars"], TestContext.Current.CancellationToken);
        var runtime = appHost.Resources.Single(resource => resource.Name == "OperatingSystem-a");
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(appHost.ExecutionContext, runtime, environment, TestContext.Current.CancellationToken);
        foreach (var callback in runtime.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        var key = DigitalBrainConfiguration.EnvironmentName(DigitalBrain.Microsoft.CSharp.CSharpDeploymentSettings.SectionName, nameof(DigitalBrain.Microsoft.CSharp.CSharpDeploymentSettings.SandboxUrl));
        Assert.IsType<EndpointReference>(environment[key]);
        var sandbox = appHost.Resources.Single(resource => resource.Name == DigitalBrain.Microsoft.CSharp.CSharpModuleHosting.ResourceName);
        // Always on: the OS never starts it on demand, and readiness never waits for it.
        Assert.DoesNotContain(sandbox.Annotations, annotation => annotation.GetType().Name == "ExplicitStartupAnnotation");
    }

    [Fact]
    public async Task ProductMatchesTheReferenceCompositionModuleSet()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.DigitalBrain_AppHost>(
            ["Parameters:Modules-digitalbrain-master-key=synthetic-composition-test-key-32chars"], TestContext.Current.CancellationToken);
        var productModules = appHost.Resources.SelectMany(resource => resource.Annotations.OfType<BrainModuleAnnotation>())
            .Select(module => module.ModuleId).Order().ToArray();
        // Composition truth lives in the OS catalog; the AppHost only hosts infrastructure,
        // so its module annotations are the hosted subset of the catalog.
        var catalogModules = DigitalBrain.OS.OsCatalog.Modules
            .Select(module => module.Id.ToString()).Order().ToArray();
        Assert.All(productModules, module => Assert.Contains(module, catalogModules));
        Assert.Contains("qdrant", productModules);
        Assert.DoesNotContain("memory", productModules);
        Assert.DoesNotContain("registry", catalogModules);
        Assert.Contains(appHost.Resources, resource => resource.Name == "Modules-openrouter-api-key"
            && resource is ParameterResource { Secret: true });
        var runtime = appHost.Resources.Single(resource => resource.Name == "OperatingSystem-a");
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(appHost.ExecutionContext, runtime, environment, TestContext.Current.CancellationToken);
        foreach (var callback in runtime.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        Assert.Equal("IDeepSeekV41Flash", environment[DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModuleOptionsSection("ai") + ":Default:Model")]);
        Assert.True(environment.ContainsKey(DigitalBrainConfiguration.EnvironmentName(
            DigitalBrain.Framework.Platform.Integrations.IntegrationSchema.Key("openrouter", "ApiKey"))));
    }
}
