using System.Security.Cryptography;
using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Apps;
using DigitalBrain.ClickHouse;
using DigitalBrain.Flutter;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Postgres;
using DigitalBrain.Qdrant;
using DigitalBrain.Salesforce;
using DigitalBrain.Supabase;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Testing.E2E;

// The reference brain is the product boot without product-owned shipped content: the same
// AppHost, the same composition, overridden only through ordinary configuration. There is no
// second list of modules anywhere — the OS catalog is the one list, and this builder rides it.
public static class ReferenceBrain
{
    // Port 1 is deliberately unserved: unused provider calls fail instead of reaching live services.
    private static readonly Uri UnconfiguredProvider = new("http://127.0.0.1:1/");

    public static E2ETestBuilder<Projects.DigitalBrain_AppHost> Create(string modelApiKey = "fixture-key", Dictionary<string, string?>? privateConfiguration = null)
        => E2ETest.For<Projects.DigitalBrain_AppHost>()
            .ConfigureModule<AIModule, AIOptions>(ai => ai.WithoutLocalModels().WithoutVoiceToText().WithoutWebSearch()
                .WithDefaultLlm<IGpt56Luna>().WithModelEndpoint(AiProvider.OpenAI, new(UnconfiguredProvider, "v1/")))
            .ConfigureModule<QdrantModule, QdrantModuleOptions>(qdrant => qdrant.Host = false)
            .ConfigureModule<PostgresModule, PostgresModuleOptions>(database => database.WithPostgres(options =>
            {
                options.DatabaseName = "digitalbrain";
                options.PersistentStorage = false;
            }))
            .ConfigureModule<ClickHouseModule, ClickHouseModuleOptions>(database => database.WithClickHouse(options =>
            {
                options.PersistentStorage = false;
            }))
            .ConfigureModule<SupabaseModule, SupabaseModuleOptions>(database => database.WithPostgres())
            .ConfigureModule<GmailModule, GmailModuleOptions>(gmail => gmail.ProfileEndpoint = new(UnconfiguredProvider, "profile"))
            .ConfigureModule<SalesforceModule, SalesforceModuleOptions>(salesforce => salesforce.WithLocalMcp(new(UnconfiguredProvider, "mcp")))
            .ConfigureModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .WithExecution(new()
            {
                PrivateConfiguration = Merge(new Dictionary<string, string?>
                {
                    ["Parameters:modules-openai-api-key"] = modelApiKey,
                    [IntegrationSchema.Key("csharp", "RunTokenKey")] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    ["Parameters:Modules-digitalbrain-master-key"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    ["Parameters:modules-gmail-client-id"] = "fixture-client",
                    ["Parameters:modules-gmail-client-secret"] = "fixture-secret",
                    [DigitalBrainConfiguration.ConnectionsTokenEndpointKey("gmail")] = new Uri(UnconfiguredProvider, "token").AbsoluteUri,
                    ["Parameters:modules-salesforce-consumer-key"] = "fixture-client",
                    ["Parameters:modules-salesforce-consumer-secret"] = "fixture-secret",
                }, privateConfiguration),
            })
            // Reference facts own their session end to end: the AppHost projects these identity
            // keys to the runtime AND names the protection container from them, so they must land
            // in the AppHost's own configuration, not just a resource's environment. After
            // WithExecution, which replaces the execution record wholesale.
            .WithHostConfiguration(IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName)), "digitalbrain.reference.session")
            .WithHostConfiguration(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName)), new IdentityHostOptions().ProtectionApplicationName)
            .WithHostConfiguration(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName)), new IdentityHostOptions().ProtectionContainerName);

    internal static Dictionary<string, string?> Merge(Dictionary<string, string?> defaults, Dictionary<string, string?>? extra)
    {
        if (extra is not null) { foreach (var item in extra) { defaults[item.Key] = item.Value; } }
        return defaults;
    }
}
