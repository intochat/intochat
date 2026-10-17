using DigitalBrain;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.OS;

// The one place the operating system's module composition is named. Modules describe
// themselves on the instance (the id in the constructor); the catalog only lists them,
// and every host of the OS registers modules through it. A distribution is a diff against
// this composition, not a copy of it. The Workshop appends modules through Generated().
public static partial class OsCatalog
{
    public static IReadOnlyList<Module> Modules { get; } = [.. BuiltIn(), .. Generated()];

    private static Module[] BuiltIn() =>
    [
        new DigitalBrain.Microsoft.Orleans.OrleansModule(),
        new DigitalBrain.AI.AIModule(),
        new DigitalBrain.ArtCraft.ArtCraftModule(),
        new DigitalBrain.ClickHouse.ClickHouseModule(),
        new DigitalBrain.Apps.AppsModule(),
        new DigitalBrain.Flutter.FlutterModule(),
        new DigitalBrain.Google.Gmail.GmailModule(),
        new DigitalBrain.Microsoft.Aspire.AspireModule(),
        new DigitalBrain.Microsoft.CSharp.CSharpModule(),
        new DigitalBrain.Microsoft.GitHub.GitHubModule(),
        new DigitalBrain.Microsoft.Playwright.PlaywrightModule(),
        new DigitalBrain.Microsoft.Windows.WindowsModule(),
        new DigitalBrain.Postgres.PostgresModule(),
        new DigitalBrain.Qdrant.QdrantModule(),
        new DigitalBrain.Salesforce.SalesforceModule(),
        // SV spikes are not part of this composition. Their modules stay in the repo.
        // new DigitalBrain.Monnet.MonnetModule(),
        // new DigitalBrain.Equifax.EquifaxModule(),
        // new DigitalBrain.RiskSeal.RiskSealModule(),
        // new DigitalBrain.Twilio.TwilioModule(),
        new DigitalBrain.Supabase.SupabaseModule(),
        new DigitalBrain.Time.TimeModule(),
        new DigitalBrain.Tools.ToolsModule(),
        new DigitalBrain.Mcp.McpModule(),
        new DigitalBrain.Circles.CirclesModule(),
        new DigitalBrain.Flame.FlameModule(),
    ];

    // Modules the Workshop has added to this OS. Declared here, implemented in
    // OsCatalog.Generated.cs so the composition grows without hand-editing this file.
    private static partial Module[] Generated();

    public static DigitalBrainServerBuilder AddOperatingSystem(this DigitalBrainServerBuilder server)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var module in Modules) { server.AddModule(module); }
        // The OS is the composition plus its apps; a distribution cannot forget them.
        server.ConfigureServices(services => services.AddOperatingSystemApps());
        return server;
    }
}
