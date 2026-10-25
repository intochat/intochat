using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Modules.AI.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Models.TypeSafe;

using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Sdk.Platform.Integrations;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.AI.Tests.Hosting;

// WithDefaultSystemOne<ILocalJev>() hosts the systemone-adapter as the `systemone` container and points the
// OS's `typesafe` integration at it; WithDefaultSystemOne<IJev>() hosts no container and no merchant key.

public sealed class SystemOneHostingFacts
{
    private static readonly string EndpointKey = IntegrationSchema.Key("typesafe", "Endpoint");
    private static readonly string ApiKeyKey = IntegrationSchema.Key("typesafe", "ApiKey");
    private static readonly string DefaultKey = AIKeys.DefaultSystemOne;

    [Fact]
    public async Task WithDefaultSystemOneLocalJevAddsTheAdapterAndPointsTheTypesafeIntegrationAtIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", new())
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai.WithDefaultSystemOne<ILocalJev>());
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        var adapter = Assert.IsType<ContainerResource>(Assert.Single(builder.Resources, resource => resource.Name == AIHostingExtensions.SystemOneResourceName));
        Assert.Contains(adapter.Annotations.OfType<HealthCheckAnnotation>(), _ => true);
        Assert.Contains(adapter.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "http" && endpoint.TargetPort == 8000);
        var backing = Assert.Single(builder.Resources.OfType<OllamaModelResource>());
        Assert.Equal("llama3.1", backing.ModelName);
        Assert.Contains(adapter.Annotations.OfType<WaitAnnotation>(), wait => ReferenceEquals(wait.Resource, backing));
        var adapterEnvironment = await EnvironmentOf(builder, adapter, ct);
        Assert.Equal("llama3.1", Assert.IsType<string>(adapterEnvironment["SYSTEMONE_MODEL"]));
        Assert.IsType<ReferenceExpression>(adapterEnvironment["OPENAI_BASE_URL"]);

        var environment = await ConfigurationOf(builder, consumer.Resource, ct);
        Assert.IsType<EndpointReference>(environment[EndpointKey]);
        Assert.Equal(AIHostingExtensions.SystemOneApiKeyLocal, Assert.IsType<string>(environment[ApiKeyKey]));
        Assert.Equal("ILocalJev", Assert.IsType<string>(environment[DefaultKey]));
        Assert.Contains(consumer.Resource.Annotations.OfType<WaitAnnotation>(), wait =>
            ReferenceEquals(wait.Resource, adapter) && wait.WaitType == WaitType.WaitUntilHealthy);
    }

    [Fact]
    public async Task WithDefaultSystemOneJevAsksForTheHostedKeyAndHostsNoContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", new())
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai.WithDefaultSystemOne<IJev>());
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.DoesNotContain(builder.Resources, resource => resource.Name == AIHostingExtensions.SystemOneResourceName);
        var environment = await ConfigurationOf(builder, consumer.Resource, ct);
        Assert.DoesNotContain(ApiKeyKey, environment.Keys);
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), resource => resource.Name.Contains("api-key", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("IJev", Assert.IsType<string>(environment[DefaultKey]));
        Assert.DoesNotContain(EndpointKey, environment.Keys);
    }

    [Fact]
    public void AnUnknownSystemOneMarkerFailsAtBind()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", new());
        var options = new AIOptions();
        options.Hosting.SystemOnes.Add("INotAJev");

        Assert.Throws<InvalidOperationException>(() => AIHostingExtensions.ConfigureDeclaration(brain, options));
    }
}
