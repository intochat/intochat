using Aspire.Hosting;
using DigitalBrain.AI;
using DigitalBrain.AI.Aspire.Hosting;
using DigitalBrain.AI.Models.FoundryLocal;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.AI.Models.OpenRouter;
using DigitalBrain.AI.Models.Ollama;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.ClickHouse;
using DigitalBrain.ClickHouse.Aspire.Hosting;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Microsoft.Aspire;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Aspire.Hosting;
using DigitalBrain.Qdrant;
using DigitalBrain.Qdrant.Aspire.Hosting;
using DigitalBrain.Salesforce;
using DigitalBrain.Salesforce.Aspire.Hosting;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

// The AppHost owns its deployment keys: the published ports, the seeded IntoChat publisher
// account, and the vendor's shipped-apps binding (leaves under
// ShippedAppsServiceCollectionExtensions.ConfigurationSection).
const string UiPortKey = "DigitalBrain:Http:UiPort";
const string McpPortKey = "DigitalBrain:Http:McpPort";
const string IntoChatAccountKey = "DigitalBrain:Auth:Accounts:0";
const string IntoChatShippedAppsKey = "DigitalBrain:Modules:apps:Shipped:intochat";

var builder = DistributedApplication.CreateBuilder(args);
var digitalBrain = builder.AddDigitalBrain("Modules", new DigitalBrainHostingOptions { ServiceId = "intochat", UseAzureStorage = true })
    .WithHttpIdentity()
    .WithHosting<AIModuleHosting, AIOptions>(ai =>
    {
        ai.WithLlm<IGpt56Luna>();
        ai.WithDefaultLlm<IDeepSeekV41Flash>();
        ai.WithDefaultEmbedding<ITextEmbedding3Small>();
        ai.WithVoiceToText<IWhisperLargeV3Turbo>();
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
    .WithHosting<CSharpModuleHosting>();

var cookieProtection = digitalBrain.AddBlobContainer(digitalBrain.Identity!.ProtectionContainerName);

var intoChatApps = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "IntoChat", "Apps"));

var runtime = builder.AddProject<Projects.DigitalBrain_OS>("OperatingSystem")
    .WithReference(digitalBrain)
    .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(IntoChatAccountKey + ":Username"), "intochat")
    .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(IntoChatAccountKey + ":DisplayName"), "IntoChat")
    .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(IntoChatShippedAppsKey + ":Path"), intoChatApps)
    .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(IntoChatShippedAppsKey + ":Account"), "intochat")
    .WaitFor(cookieProtection)
    .WithHttpEndpoint(
        port: builder.Configuration.GetValue(UiPortKey, 5080),
        name: "http",
        isProxied: false)
    .WithHttpHealthCheck("/health", endpointName: "http");

// Development only: a known IntoChat password for one-step switching, and publishing in the shell
// writes the apps folder back. A published deployment ships neither.
if (builder.ExecutionContext.IsRunMode)
{
    runtime
        .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(IntoChatAccountKey + ":Password"),
            builder.Configuration[IntoChatAccountKey + ":Password"] ?? "intochat-development")
        .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(IntoChatShippedAppsKey + ":Writable"), "true");
}

builder.AddProject<Projects.DigitalBrain_Mcp>("digitalbrain-mcp")
    .WithReference(digitalBrain.AsClient(shareHttpIdentity: true))
    .WithHttpEndpoint(port: builder.Configuration.GetValue(McpPortKey, 5081), name: "http", isProxied: false)
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WaitFor(runtime);

builder.Build().Run();
