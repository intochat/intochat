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

// Stage B (Task 18): the trust cluster (sequencer, node directory, keyrings, identity, ledger and the Marketplace
// catalog) is its own host and Orleans cluster on the orleans integration's tables and blobs, under its own
// cluster id and container; each OS silo below is a machine that reaches it only through a private gateway
// client (H2, R34). The trust host gets no other credential (R30) and no features copy (R56).
const int TrustGatewayPort = 30100;
Func<string, string> env = DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName;
var trust = builder.AddProject<Projects.DigitalBrain_Trust>("trust").AsTrustCluster(digitalBrain, TrustGatewayPort);
// One pairing secret for every OS machine of this deployment (R52): the second machine joins the owner's account.
var pairingSecret = builder.AddParameter("machine-pairing-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);

// Each role runs named instances ({role}-a, {role}-b) on dynamic ports behind a YARP front on the fixed port;
// the MCP role runs two, the OS role one (below). First boot runs from source; once a candidate is promoted, {repo}/.digitalbrain/live.json names the
// published build every instance runs, and a restart moves an instance onto the next one.
var workdir = AspireModuleHosting.WorkdirOf(builder);
// While the part log is on, one OS silo is the own machine (R23): two Role=Own silos with separate machine
// identities would contest every part's lease. A second instance returns with Stage B's host shape.
var silos = AspireModuleHosting.InstanceNames("OperatingSystem").Take(1).Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "os", "DigitalBrain.OS.dll") is { } live
        // A live build reads the features published with it (AddLiveBuild), not the working tree's.
        ? Silo(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS"))
        : Silo(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.DigitalBrain_OS>(name))
            .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(featuresKey), features))).ToArray();
var osFront = AspireModuleHosting.AddFront(builder, "OperatingSystem", uiPort, silos.Select(silo => silo.GetEndpoint("http")));

// The second machine (R40): its own directory, identity and Orleans cluster, the owner's role, fixed silo and
// gateway ports beside the first machine's defaults, and its own HTTP port serving its own /mcp. It is not behind
// the front: it is another machine of the same owner, reached directly.
var machineB = builder.AddProject<Projects.DigitalBrain_OS>("OperatingSystem-b")
    .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(featuresKey), features)
    .WithHttpEndpoint(port: 5095, name: "http", isProxied: false)
    .WithEnvironment("Orleans__ClusterId", "digitalbrain-machine-b")
    .WithEnvironment("Orleans__Endpoints__SiloPort", "11112")
    .WithEnvironment("Orleans__Endpoints__GatewayPort", "30001");
Silo(machineB);
// The shell reaches the kernel through the front, so a roll restarting one silo never drops it.
digitalBrain.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(osFront.Resource));

var mcps = AspireModuleHosting.InstanceNames("digitalbrain-mcp").Select(name =>
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
        .WithEnvironment(env(TrustClusterHosting.GatewayKey), TrustClusterHosting.GatewayOf(TrustGatewayPort))
        .WithEnvironment(env("DigitalBrain:Platform:Parts:PairingSecret"), pairingSecret)
        .WaitFor(trust)
        .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName("DigitalBrain:Platform:Mesh:SeedNodes"), "24")
        .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName("DigitalBrain:Platform:Parts:Role"), "Own")
        .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName("DigitalBrain:Platform:Parts:MachineDirectory"),
            Path.Combine(workdir, "machines", silo.Resource.Name))
        .WaitFor(cookieProtection)
        .WithHttpHealthCheck("/health", endpointName: "http")
        // Hands one part to another machine (R40): POST /machine/parts/{part}/release as this machine's owner.
        .WithHttpCommand("/machine/parts/release", "Release part", commandName: "release-part", endpointName: "http", commandOptions: new HttpCommandOptions
        {
            Description = "Release one part so another machine of this owner can take it.",
            Arguments = [new InteractionInput { Name = "part", Label = "Part", InputType = InputType.Text, Required = true }],
            PrepareRequest = request =>
            {
                var part = request.Arguments.GetString("part") ?? "";
                request.Request.RequestUri = new Uri(request.Request.RequestUri!, $"/machine/parts/{Uri.EscapeDataString(part)}/release");
                return Task.CompletedTask;
            },
        });
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(silo.Resource);
}

// The MCP server is an Orleans client: it starts once every silo instance is up.
IResourceBuilder<IResourceWithEndpoints> Mcp<T>(IResourceBuilder<T> mcp) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport
{
    mcp.WithReference(digitalBrain.AsClient(shareHttpIdentity: true))
        .WithHttpHealthCheck("/health", endpointName: "http");
    foreach (var silo in silos) { mcp.WaitFor(silo); }
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(mcp.Resource);
}
