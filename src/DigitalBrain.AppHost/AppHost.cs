using Aspire.Hosting;
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
var digitalBrain = builder.AddDigitalBrain("Modules", DigitalBrainHostingOptions.Named(builder.Configuration, "intochat", useAzureStorage: true))
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
    .WithHosting<GitHubModuleHosting>()
    .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunDesktopApp())
    .WithHosting<AspireModuleHosting>()
    .WithHosting<CSharpModuleHosting>()
    // PhotoCraft and FilmCraft: headless MCP servers plus their web builds, which the shell's browser may drive.
    .WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps())
    .WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins);

var cookieProtection = digitalBrain.AddBlobContainer(digitalBrain.Identity!.ProtectionContainerName);

// The repository's ./features: os and intochat apps, loaded by the OS at boot.
var features = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "features"));

var runtime = builder.AddProject<Projects.DigitalBrain_OS>("OperatingSystem")
    .WithReference(digitalBrain)
    .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(featuresKey), features)
    .WaitFor(cookieProtection)
    .WithHttpEndpoint(
        port: builder.Configuration.GetValue(UiPortKey, 5080),
        name: "http",
        isProxied: false)
    .WithHttpHealthCheck("/health", endpointName: "http");

builder.AddProject<Projects.DigitalBrain_Mcp>("digitalbrain-mcp")
    .WithReference(digitalBrain.AsClient(shareHttpIdentity: true))
    .WithHttpEndpoint(port: builder.Configuration.GetValue(McpPortKey, 5081), name: "http", isProxied: false)
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WaitFor(runtime);

builder.Build().Run();
