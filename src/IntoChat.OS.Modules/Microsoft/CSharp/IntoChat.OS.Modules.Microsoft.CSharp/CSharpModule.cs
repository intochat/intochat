using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Microsoft.CSharp.Authoring;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using IntoChat.OS.Microsoft.CSharp.Edge;
using IntoChat.OS.Microsoft.CSharp.Files;
using IntoChat.OS.Microsoft.CSharp.Files.Signals;
using IntoChat.OS.Microsoft.CSharp.Runtime;
using IntoChat.OS.Microsoft.CSharp.Sessions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace IntoChat.OS.Microsoft.CSharp;

public sealed class CSharpModule() : Module<CSharpOptions>(new ModuleId("csharp")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Microsoft.CSharp.Files.ICSharpFile).Assembly;

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
        var options = builder.Configuration.GetModuleOptions<CSharpOptions>("csharp");
        var authoring = options.Authoring;
        builder.Services.TryAddSingleton<CSharpToolService>();
        builder.Services.TryAddSingleton<IntoChat.OS.Apps.IScriptSandbox, CSharpScriptSandbox>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<SessionTokens>();
        builder.Services.AddSingleton<IIntegrationStartup>(services => services.GetRequiredService<SessionTokens>());
        builder.Services.TryAddSingleton<CSharpContractDiscovery>();
        builder.Services.TryAddSingleton<CSharpScriptCheck>();
        builder.Services.TryAddSingleton<IBehaviorAssemblies>(provider =>
            provider.GetKeyedService<BlobServiceClient>(IntoChatOSNames.GrainState) is { } storage
                ? new BlobBehaviorAssemblies(storage) : new MemoryBehaviorAssemblies());
        // The session pool authenticates with the deployment's identity; the local container needs none.
        if (!string.IsNullOrWhiteSpace(options.SessionPoolEndpoint))
        { builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential()); }
        builder.Services.AddHttpClient<SessionRunner>();
        builder.Services.TryAddSingleton<SessionStreams>();
        builder.Services.TryAddKeyedSingleton<IHostApplicationLifetime>(typeof(CSharpModule),
            (services, _) => services.GetRequiredService<IHostApplicationLifetime>());
        builder.Services.TryAddSingleton<DeferredRuntimeStarts>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DeferredRuntimeStarts>());
        builder.Services.TryAddSingleton<RuntimeDirectory>();
        builder.Services.TryAddSingleton<IWorkContinuation, RuntimeWorkContinuation>();
        builder.Services.TryAddTransient<RuntimeEdge>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<BrainRuntimeNeuron>, BrainRuntimeActivation>());
        builder.AddIncomingGrainCallFilter<BrainRunsGuard>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IntoChat.OS.Framework.Platform.Identity.ISignInHandler, RuntimeSignIn>());
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
