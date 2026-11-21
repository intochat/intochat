using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ModuleSecretProjectionFacts
{
    [DigitalBrain.Modules.Module("dual-secrets")]
    private sealed class DualSecretsModule : DigitalBrain.Modules.Module
    {
        public static IntegrationSchema Integration { get; } = new("dual-oauth", "Dual OAuth", ["ClientId", "ClientSecret"], []);

        public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
            Provides: [],
            Requires: [],
            Needs: [],
            Settings: [],
            Secrets: [DigitalBrain.Modules.Secret.Named("signing-key")]);
    }

    [Fact]
    public async Task WithModuleProjectsBothIntegrationSchemaAndManifestSecretsIdempotently()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration["IntoChatOS:Integrations:dual-oauth:ClientId"] = "id-123";
        builder.Configuration["IntoChatOS:Integrations:dual-oauth:ClientSecret"] = "secret-456";
        builder.Configuration["IntoChatOS:Modules:dual-secrets:Secrets:signing-key"] = "key-789";

        var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
            .WithModule<DualSecretsModule>();
        ModuleSecretProjection.Project(brain, typeof(DualSecretsModule));

        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, environment, TestContext.Current.CancellationToken);
        foreach (var callback in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        Assert.Contains(environment.Keys, key => key.EndsWith("ClientId", StringComparison.Ordinal));
        Assert.Contains(environment.Keys, key => key.EndsWith("ClientSecret", StringComparison.Ordinal));
        Assert.Contains(environment.Keys, key => key.EndsWith("signing-key", StringComparison.Ordinal));
    }
}
