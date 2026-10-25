using System.Security.Cryptography;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.AppHost;
using IntoChat.OS.Modules.ClickHouse;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Platform.Integrations;
using IntoChat.OS.Modules.Google.Gmail;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Qdrant;
using IntoChat.OS.Modules.Salesforce;
using IntoChat.OS.Modules.Supabase;

namespace IntoChat.OS.Kernel.Testing;

// The reference brain is the product boot without product-owned shipped content: the same
// AppHost, the same composition, overridden only through ordinary configuration. There is no
// second list of modules anywhere — the OS catalog is the one list, and this builder rides it.
public static class ReferenceBrain
{
    // Port 1 is deliberately unserved: unused provider calls fail instead of reaching live services.
    private static readonly Uri UnconfiguredProvider = new("http://127.0.0.1:1/");

    public static TestBrain Create(string modelApiKey = "fixture-key", Dictionary<string, string?>? privateConfiguration = null)
    {
        var brain = TestBrain.Create().On(new AspireTarget<Projects.IntoChat_OS_AppHost>())
            // The AppHost runs two machines; A holds the core and is the one the fronts and MCPs route to.
            .WithExecution(new TestExecutionOptions { ServerResourceName = "OperatingSystem-a" })
            .WithOptions<AIModule, AIOptions>(ApplyReferenceAi)
            .WithOptions<QdrantModule, QdrantModuleOptions>(qdrant => qdrant.Host = false)
            .WithOptions<PostgresModule, PostgresModuleOptions>(database => database.WithPostgres(options =>
            {
                options.DatabaseName = "intochat_os";
                options.PersistentStorage = false;
            }))
            .WithOptions<ClickHouseModule, ClickHouseModuleOptions>(database => database.WithClickHouse(options =>
            {
                options.PersistentStorage = false;
            }))
            .WithOptions<SupabaseModule, SupabaseModuleOptions>(database => database.WithPostgres())
            .WithOptions<GmailModule, GmailModuleOptions>(gmail => gmail.ProfileEndpoint = new(UnconfiguredProvider, "profile"))
            .WithOptions<SalesforceModule, SalesforceModuleOptions>(salesforce => salesforce.WithLocalMcp(new(UnconfiguredProvider, "mcp")))
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            // Reference facts own their session end to end: the AppHost projects these identity
            // keys to the runtime AND names the protection container from them, so they must land
            // in the AppHost's own configuration, not just a resource's environment.
            .WithHostConfiguration(IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName)), "intochatos.reference.session")
            .WithHostConfiguration(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName)), new IdentityHostOptions().ProtectionApplicationName)
            .WithHostConfiguration(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName)), new IdentityHostOptions().ProtectionContainerName);
        foreach (var (key, value) in Merge(new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("openai", "ApiKey")] = modelApiKey,
            [IntegrationSchema.Key("csharp", "RunTokenKey")] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Parameters:Modules-intochat-os-master-key"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            [IntoChatOSConfiguration.ConnectionsTokenEndpointKey("gmail")] = new Uri(UnconfiguredProvider, "token").AbsoluteUri,
        }, privateConfiguration))
        { brain.WithConfiguration(key, value); }
        return brain;
    }

    public static void ApplyReferenceAi(AIOptions ai)
    {
        ArgumentNullException.ThrowIfNull(ai);
        RunProfile.CopyOnto(ai, RunProfile.Reference.Ai);
    }

    internal static Dictionary<string, string?> Merge(Dictionary<string, string?> defaults, Dictionary<string, string?>? extra)
    {
        if (extra is not null) { foreach (var item in extra) { defaults[item.Key] = item.Value; } }
        return defaults;
    }
}
