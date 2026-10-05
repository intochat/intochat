using DigitalBrain;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Framework;

namespace DigitalBrain.OS;

// The one place the operating system's module composition is named. Modules describe
// themselves on the instance (the id in the constructor); the catalog only lists them,
// and every host of the OS registers modules through it. A distribution is a diff against
// this composition, not a copy of it.
public static class OsCatalog
{
    public static IReadOnlyList<Module> Modules { get; } =
    [
        new DigitalBrain.AI.AIModule(),
        new DigitalBrain.ClickHouse.ClickHouseModule(),
        new DigitalBrain.Apps.AppsModule(),
        new DigitalBrain.Assistant.AssistantModule(),
        new DigitalBrain.Compute.ComputeModule(),
        new DigitalBrain.Registry.RegistryModule(),
        new DigitalBrain.Files.FilesModule(),
        new DigitalBrain.Flutter.FlutterModule(),
        new DigitalBrain.Google.Gmail.GmailModule(),
        new DigitalBrain.Microsoft.Aspire.AspireModule(),
        new DigitalBrain.Microsoft.CSharp.CSharpAuthoringModule(),
        new DigitalBrain.Microsoft.CSharp.CSharpModule(),
        new DigitalBrain.Microsoft.GitHub.GitHubModule(),
        new DigitalBrain.Microsoft.Playwright.PlaywrightModule(),
        new DigitalBrain.Postgres.PostgresModule(),
        new DigitalBrain.Qdrant.QdrantModule(),
        new DigitalBrain.Salesforce.SalesforceModule(),
        new DigitalBrain.Supabase.SupabaseModule(),
        new DigitalBrain.Time.TimeModule(),
    ];

    public static DigitalBrainServerBuilder AddOperatingSystem(this DigitalBrainServerBuilder server)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var module in Modules) { server.AddModule(module); }
        // The OS is the composition plus its apps; a distribution cannot forget them.
        server.ConfigureServices(services => services.AddOperatingSystemApps());
        return server;
    }
}
