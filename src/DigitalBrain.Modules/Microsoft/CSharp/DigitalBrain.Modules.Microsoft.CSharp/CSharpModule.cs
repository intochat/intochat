using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Files.Signals;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.CSharp;

public sealed class CSharpModule() : Module<CSharpOptions>(new ModuleId("csharp")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Microsoft.CSharp.Files.ICSharpFile).Assembly;

    public static IntegrationSchema Integration => IntegrationSchema.For("csharp", "CSharp run tokens").RequiresSecret("RunTokenKey");

    // The sandbox is the external system behind ICSharpFile, the way Gmail is behind IGmail. Declared so the
    // catalog knows it, not offered to users: its only credential is the deployment's session-token key
    // (still named RunTokenKey), no connection neuron serves it, and Platform requires one auth method.
    public static ConnectorDefinition Connector { get; } = new("csharp", "C# sandbox", ["run-token"], [])
    {
        ExternalSystem = "C# sandbox",
        OptionalIntegrations = ["csharp"],
        Neurons = [typeof(ICSharpFile)],
        Signals = [typeof(CSharpFileChanged)],
    };

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var authoring = builder.Configuration.GetModuleOptions<CSharpOptions>("csharp").Authoring;
        builder.Services.AddOptions<CSharpDeploymentSettings>().BindConfiguration(CSharpDeploymentSettings.SectionName);
        builder.Services.TryAddSingleton<CSharpToolService>();
        builder.Services.TryAddSingleton<DigitalBrain.Apps.IScriptSandbox, CSharpScriptSandbox>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<SessionTokens>();
        builder.Services.AddSingleton<IIntegrationStartup>(services => services.GetRequiredService<SessionTokens>());
        builder.Services.TryAddSingleton<CSharpContractDiscovery>();
        builder.Services.TryAddSingleton<CSharpScriptCheck>();
        builder.Services.TryAddSingleton<IBehaviorAssemblies>(provider =>
            provider.GetKeyedService<BlobServiceClient>(DigitalBrainNames.GrainState) is { } storage
                ? new BlobBehaviorAssemblies(storage) : new MemoryBehaviorAssemblies());
        // The session pool authenticates with the deployment's identity; the local container needs none.
        if (!string.IsNullOrWhiteSpace(builder.Configuration[CSharpDeploymentSettings.SectionName + ":SessionPoolEndpoint"]))
        { builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential()); }
        builder.Services.AddHttpClient<SessionRunner>();
        builder.Services.TryAddSingleton<SessionStreams>();
        builder.Services.TryAddKeyedSingleton<IHostApplicationLifetime>(typeof(CSharpModule),
            (services, _) => services.GetRequiredService<IHostApplicationLifetime>());
        builder.Services.TryAddSingleton<DeferredRuntimeStarts>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DeferredRuntimeStarts>());
        builder.Services.TryAddTransient<RuntimeEdge>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<BrainRuntimeNeuron>, BrainRuntimeActivation>());
        builder.AddIncomingGrainCallFilter<BrainRunsGuard>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Framework.Platform.Identity.ISignInHandler, RuntimeSignIn>());
        if (authoring)
        {
            builder.Services.TryAddScoped(provider => provider.GetRequiredService<CSharpToolService>().ForScope(BrainScope.CurrentId()));
        }
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapRuntimeEdge();
        if (endpoints.ServiceProvider.GetRequiredService<IOptions<CSharpOptions>>().Value.Authoring) { CSharpAuthoringEndpoints.Map(endpoints); }
    }
}
