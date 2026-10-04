using DigitalBrain.Aspire.Server;

namespace DigitalBrain.OS;

// The one place the operating system's module composition is named. Modules describe
// themselves ([ModuleId], [ModuleHosting], [ModuleDeployment]); the catalog only lists them,
// and every host of the OS registers modules through it. A distribution is a diff against
// this composition, not a copy of it.
public static class OsCatalog
{
    public static IReadOnlyList<Type> Modules { get; } =
    [
        typeof(DigitalBrain.AI.AIModule),
        typeof(DigitalBrain.ClickHouse.ClickHouseModule),
        typeof(DigitalBrain.Apps.AppsModule),
        typeof(DigitalBrain.Assistant.AssistantModule),
        typeof(DigitalBrain.Compute.ComputeModule),
        typeof(DigitalBrain.Registry.RegistryModule),
        typeof(DigitalBrain.Specs.SpecsModule),
        typeof(DigitalBrain.Files.FilesModule),
        typeof(DigitalBrain.Flutter.FlutterModule),
        typeof(DigitalBrain.Google.Gmail.GmailModule),
        typeof(DigitalBrain.Microsoft.Aspire.AspireModule),
        typeof(DigitalBrain.Microsoft.CSharp.CSharpAuthoringModule),
        typeof(DigitalBrain.Microsoft.CSharp.CSharpModule),
        typeof(DigitalBrain.Microsoft.GitHub.GitHubModule),
        typeof(DigitalBrain.Microsoft.Playwright.PlaywrightModule),
        typeof(DigitalBrain.Postgres.PostgresModule),
        typeof(DigitalBrain.Qdrant.QdrantModule),
        typeof(DigitalBrain.Salesforce.SalesforceModule),
        typeof(DigitalBrain.Supabase.SupabaseModule),
        typeof(DigitalBrain.Time.TimeModule),
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
