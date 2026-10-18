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
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions { PersistentStorage = false });
        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, environment, TestContext.Current.CancellationToken);
        foreach (var callback in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        Assert.Equal("Production", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Production", environment["DOTNET_ENVIRONMENT"]);
    }
}
