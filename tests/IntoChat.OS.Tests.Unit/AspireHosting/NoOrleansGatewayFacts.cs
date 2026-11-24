using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

// No OS host is an Orleans cluster client, so the AppHost allocates, publishes and configures no gateway for a kernel: the
// kernel keeps its silo port (the silo needs it) and nothing else of Orleans' listeners. Read from the app model; nothing starts.
public sealed class NoOrleansGatewayFacts
{
    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task TheKernelProject_HasASiloEndpoint_AndNoGateway(string topology)
    {
        var os = AppHostChainFacts.Compose(topology);
        var kernel = Assert.Single(os.ApplicationBuilder.Resources, static r => r.Name == "os");

        AssertNoGateway(kernel, await Environment(os, kernel));
    }

    [Fact]
    public async Task TheKernelContainer_HasASiloEndpoint_AndNoGateway()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--IntoChatOS:Topology=local", "--IntoChatOS:Shell:Enabled=false", "--IntoChatOS:Ollama:AutoContainer=false"],
            DisableDashboard = true,
        });
        var os = builder.AddIntoChatOS("Modules");
        var container = os.AddKernelContainer();

        AssertNoGateway(container.Resource, await Environment(os, container.Resource));
    }

    private static void AssertNoGateway(IResource kernel, Dictionary<string, object> environment)
    {
        var endpoints = kernel.Annotations.OfType<EndpointAnnotation>().Select(static e => e.Name).ToArray();
        Assert.Contains(RuntimeHosting.OrleansSiloEndpoint, endpoints);
        Assert.DoesNotContain(RuntimeHosting.OrleansGatewayEndpoint, endpoints);
        Assert.True(!environment.TryGetValue(RuntimeHosting.OrleansGatewayPortVariable, out var port) || port is "0",
            $"The kernel is told to open a gateway on {port}.");
    }

    private static async Task<Dictionary<string, object>> Environment(IntoChatOSBuilder os, IResource kernel)
    {
        var environment = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var context = new EnvironmentCallbackContext(os.ApplicationBuilder.ExecutionContext, kernel, environment, TestContext.Current.CancellationToken);
        foreach (var callback in kernel.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        return environment;
    }
}
