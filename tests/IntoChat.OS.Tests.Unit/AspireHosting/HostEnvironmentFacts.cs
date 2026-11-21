using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class HostEnvironmentFacts
{
    [Fact]
    public async Task TheHostingEnvironmentNamesTheSiloProcessEnvironment()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IntoChatOSHostingOptions.EnvironmentKey] = "Production";
        var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions());
        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, environment, TestContext.Current.CancellationToken);
        foreach (var callback in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        Assert.Equal("Production", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Production", environment["DOTNET_ENVIRONMENT"]);
    }

    [Fact]
    public async Task AddIntoChatOSContainer_ConfiguresHttpEndpointHealthCheckAndHelpers()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var container = builder.AddIntoChatOSContainer("os", httpPort: 5080)
            .WithBootstrapOwner("operator", "bootstrap-secret")
            .WithTopology("cloud");

        Assert.Equal("ghcr.io/intochat/os", container.Resource.Annotations.OfType<ContainerImageAnnotation>().Single().Image);
        Assert.Equal("latest", container.Resource.Annotations.OfType<ContainerImageAnnotation>().Single().Tag);

        var endpoint = Assert.Single(container.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
        Assert.Equal(5080, endpoint.Port);
        Assert.Equal(8080, endpoint.TargetPort);

        var healthCheck = Assert.Single(container.Resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.NotNull(healthCheck.Key);

        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, container.Resource, environment, TestContext.Current.CancellationToken);
        foreach (var callback in container.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Equal("operator", environment["IntoChatOS__Auth__Username"]);
        Assert.Equal("bootstrap-secret", environment["IntoChatOS__Auth__Password"]);
        Assert.Equal("cloud", environment["IntoChatOS__Topology"]);
    }
}
