using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.AI;
using DigitalBrain.AI.Aspire.Hosting;
using DigitalBrain.AI.Models.FoundryLocal;
using DigitalBrain.AI.Models.Ollama;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.AI.Models.OpenRouter;
using DigitalBrain.ArtCraft;
using DigitalBrain.ArtCraft.Aspire.Hosting;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.ClickHouse;
using DigitalBrain.ClickHouse.Aspire.Hosting;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Microsoft.Aspire;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Microsoft.Playwright;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Aspire.Hosting;
using DigitalBrain.Qdrant;
using DigitalBrain.Qdrant.Aspire.Hosting;
using DigitalBrain.Salesforce;
using DigitalBrain.Salesforce.Aspire.Hosting;
using DigitalBrain.Monnet.Aspire.Hosting;
using DigitalBrain.Equifax.Aspire.Hosting;
using DigitalBrain.RiskSeal.Aspire.Hosting;
using DigitalBrain.Twilio.Aspire.Hosting;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

// The AppHost owns its deployment keys: the published ports, and the features folder it hands the OS
// (the apps module's Features option, under DigitalBrainConfiguration.ModuleOptionsSection).
const string UiPortKey = "DigitalBrain:Http:UiPort";
const string McpPortKey = "DigitalBrain:Http:McpPort";
var featuresKey = DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.ModuleOptionsSection("apps") + ":Features";

var builder = DistributedApplication.CreateBuilder(args);
// `aspire run -- --DigitalBrain:Profile local` composes profiles/*.json; dev is today's composition.
builder.Configuration.AddDigitalBrainProfiles(builder.AppHostDirectory);
if (builder.Configuration["DigitalBrain:ConfigFile"] is { Length: > 0 } configFile)
{ builder.Configuration.AddJsonFile(configFile, optional: false); }
var shellEnabled = builder.Configuration.GetValue("DigitalBrain:Shell:Enabled", true);
var digitalBrain = builder.AddDigitalBrain("Modules", DigitalBrainHostingOptions.Named(builder.Configuration, "intochat"))
    .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite())
    .WithHttpIdentity()
    .WithHosting<AIModuleHosting, AIOptions>(ai =>
    {
        ai.WithLlm<IGpt56Luna>();
        ai.WithDefaultLlm<IDeepSeekV41Flash>();
        ai.WithDefaultEmbedding<ITextEmbedding3Small>();
        ai.WithVoiceToText<IWhisperLargeV3Turbo>();
        ai.WithDefaultSystemOne<ILocalJev>();
        ai.WithTavilySearch();
    })
    .WithHosting<QdrantModuleHosting, QdrantModuleOptions>(qdrant => qdrant.WithHostedQdrant())
    .WithHosting<ClickHouseModuleHosting, ClickHouseModuleOptions>(database => database.WithClickHouse())
    .WithHosting<SupabaseModuleHosting, SupabaseModuleOptions>(database => database.WithConnection("supabase"))
    .WithHosting<PostgresModuleHosting, PostgresModuleOptions>(database => database.WithPostgres(options => options.DatabaseName = "digitalbrain"))
    .WithHosting<GmailModuleHosting, GmailModuleOptions>(gmail => gmail.WithGmail())
    .WithHosting<SalesforceModuleHosting, SalesforceModuleOptions>(salesforce => salesforce.WithHostedMcp())
    // SV integration spikes (docs/superpowers/specs/2026-10-11-sv-integrations-design.md): each asks for its sandbox credentials.
    .WithHosting<MonnetModuleHosting>()
    .WithHosting<EquifaxModuleHosting>()
    .WithHosting<RiskSealModuleHosting>()
    .WithHosting<TwilioModuleHosting>()
    .WithHosting<GitHubModuleHosting>();
if (shellEnabled) { digitalBrain = digitalBrain.WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunDesktopApp()); }
digitalBrain = digitalBrain
    .WithHosting<AspireModuleHosting>()
    .WithHosting<CSharpModuleHosting>()
    // PhotoCraft and FilmCraft: headless MCP servers plus their web builds, which the shell's browser may drive.
    .WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps())
    .WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins);

var cookieProtection = digitalBrain.AddBlobContainer(digitalBrain.Identity!.ProtectionContainerName);

// The repository's ./features: os and intochat apps, loaded by the OS at boot.
var features = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "features"));

var uiPort = builder.Configuration.GetValue(UiPortKey, 5080);
var mcpPort = builder.Configuration.GetValue(McpPortKey, 5081);

// One runtime owns this deployment; devices are connections to external capabilities.
var workdir = AspireModuleHosting.WorkdirOf(builder);
var silos = AspireModuleHosting.InstanceNames("OperatingSystem").Take(1).Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "os", "DigitalBrain.OS.dll") is { } live
        // A live build reads the features published with it (AddLiveBuild), not the working tree's.
        // R76: A is the core holder, the machine the fronts and MCPs route to; B never races it for the core.
        ? Primary(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS"))
        : Primary(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.DigitalBrain_OS>(name))
            .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(featuresKey), features))).ToArray();
var osFront = AspireModuleHosting.AddFront(builder, "OperatingSystem", uiPort, silos.Select(silo => silo.GetEndpoint("https")));

// The front keeps a stable TLS endpoint across a single-runtime restart.
digitalBrain.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(osFront.Resource));

var mcps = AspireModuleHosting.InstanceNames("digitalbrain-mcp").Take(1).Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "mcp", "DigitalBrain.Mcp.dll") is { } live
        ? Mcp(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS"))
        : Mcp(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.DigitalBrain_Mcp>(name)))).ToArray();
AspireModuleHosting.AddFront(builder, "digitalbrain-mcp", mcpPort, mcps.Select(mcp => mcp.GetEndpoint("http")));

builder.Build().Run();

IResourceBuilder<IResourceWithEndpoints> Silo<T>(IResourceBuilder<T> silo) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport
{
    // Stage A: the OS silo is an own machine running the part log in its own directory, over a mesh of
    // platform seed nodes (Q25).
    silo.WithReference(digitalBrain)
        .WithEnvironment("DigitalBrain__Platform__Storage__Directory", Path.Combine(workdir, "brain-store"))
        .WithEnvironment("DigitalBrain__Platform__Parts__MachineDirectory", Path.Combine(workdir, "runtime"))
        .WaitFor(cookieProtection)
        .WithHttpHealthCheck("/health", endpointName: "http");
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(silo.Resource);
}

// R76: A is the core holder, the machine the fronts and MCPs route to. Its Orleans ports are allocated after the
// brain reference (Task 22), in the deployment's cluster the MCPs join.
IResourceBuilder<IResourceWithEndpoints> Primary<T>(IResourceBuilder<T> silo) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithArgs
{
    // TLS negotiates HTTP/2 for the native shell through YARP, while HTTP remains for health and sandbox callbacks.
    // Projects compose ASPNETCORE_URLS from endpoints; an explicit port variable would be overridden by it.
    silo.WithHttpsEndpoint(name: "https", env: silo.Resource is ProjectResource ? null : "ASPNETCORE_HTTPS_PORTS", isProxied: false);
#pragma warning disable ASPIRECERTIFICATES001
    silo.WithHttpsDeveloperCertificate();
#pragma warning restore ASPIRECERTIFICATES001
    var primary = Silo(silo.AsCoreHolder());
    silo.WithAllocatedOrleansPorts();
    return primary;
}

// The MCP server is an Orleans client: it starts once every silo instance is up.
IResourceBuilder<IResourceWithEndpoints> Mcp<T>(IResourceBuilder<T> mcp) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport
{
    mcp.WithReference(digitalBrain.AsClient(shareHttpIdentity: true))
        .WithHttpHealthCheck("/health", endpointName: "http");
    foreach (var silo in silos) { mcp.WaitFor(silo); }
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(mcp.Resource);
}
