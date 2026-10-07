using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Files.Signals;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.CSharp;

public sealed class CSharpModule() : Module<CSharpOptions>(new ModuleId("csharp")), IHttpModule
{
    public static IntegrationSchema Integration => IntegrationSchema.For("csharp", "CSharp run tokens").RequiresSecret("RunTokenKey");

    // The sandbox is the external system behind ICSharpFile, the way Gmail is behind IGmail. Declared so the
    // catalog knows it, not offered to users: its only credential is the deployment's run-token key, no
    // connection neuron serves it, and Platform requires one auth method, which "run-token" names.
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
        builder.AddIncomingGrainCallFilter<CSharpAppBindingGuard>();
        builder.Services.AddOptions<CSharpOptions>()
            .Configure<IConfiguration>((target, configuration) => configuration.PopulateModuleOptions("csharp", target));
        builder.Services.AddOptions<CSharpDeploymentSettings>().BindConfiguration(CSharpDeploymentSettings.SectionName);
        builder.Services.TryAddSingleton<CSharpCatalogStore>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<CSharpFileNeuron>, CSharpFileActivation>());
        builder.Services.TryAddSingleton<CSharpToolService>();
        builder.Services.TryAddSingleton<DigitalBrain.Apps.IScriptSandbox, CSharpScriptSandbox>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<RunTokens>();
        builder.Services.AddSingleton<IIntegrationStartup>(services => services.GetRequiredService<RunTokens>());
        builder.Services.TryAddSingleton<SessionTokens>();
        builder.Services.AddSingleton<IIntegrationStartup>(services => services.GetRequiredService<SessionTokens>());
        builder.Services.TryAddSingleton<ScriptRunEnvironment>();
        // Explicit: DI would resolve the assemblies parameter as an empty IEnumerable<Assembly>.
        builder.Services.TryAddSingleton(provider => new ScriptContracts(provider.GetRequiredService<ContractCatalog>()));
        builder.Services.TryAddSingleton<IContractVocabulary>(provider => new ContractVocabulary(provider.GetRequiredService<ContractCatalog>()));
        builder.Services.TryAddTransient<ScriptEdge>();
        builder.Services.TryAddSingleton<CSharpDirectives>();
        builder.Services.TryAddSingleton<CSharpContractDiscovery>();
        builder.Services.TryAddSingleton<CSharpScriptCheck>();
        builder.Services.TryAddSingleton<IBehaviorAssemblies>(provider =>
            provider.GetKeyedService<BlobServiceClient>(DigitalBrainNames.GrainState) is { } storage
                ? new BlobBehaviorAssemblies(storage) : new MemoryBehaviorAssemblies());
        if (!string.IsNullOrWhiteSpace(builder.Configuration[CSharpDeploymentSettings.SectionName + ":SessionPoolEndpoint"]))
        {
            builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
            builder.Services.AddHttpClient<ICSharpRunner, SessionPoolRunner>();
        }
        else { builder.Services.AddHttpClient<ICSharpRunner, AspireSandboxRunner>(); }
        builder.Services.AddHttpClient<SessionRunner>();
        if (authoring)
        {
            builder.Services.TryAddScoped(provider => provider.GetRequiredService<CSharpToolService>().ForScope(BrainScope.CurrentId()));
        }
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapScriptEdge();
        if (endpoints.ServiceProvider.GetRequiredService<IOptions<CSharpOptions>>().Value.Authoring) { CSharpAuthoringEndpoints.Map(endpoints); }
    }
}
