using IntoChat.OS;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel;

// The one place the operating system's module composition is named. Modules describe
// themselves on the instance (the id in the constructor); the catalog only lists them,
// and every host of the OS registers modules through it. A distribution is a diff against
// this composition, not a copy of it. The Workshop appends modules through Generated().
public static partial class OsCatalog
{
    public static IReadOnlyList<Module> Modules { get; } = [.. BuiltIn(), .. Generated()];

    // The composition a test process can host by itself: every module that needs no container.
    public static IReadOnlyList<Module> InProcessModules { get; } = [.. Modules.Where(module => !NeedsItsOwnMachine(module))];

    private static bool NeedsItsOwnMachine(Module module) => module
        is IntoChat.OS.Postgres.PostgresModule        // a Postgres container
        or IntoChat.OS.ClickHouse.ClickHouseModule    // a ClickHouse container
        or IntoChat.OS.Supabase.SupabaseModule        // Postgres-backed: the Postgres container
        or IntoChat.OS.Qdrant.QdrantModule            // a Qdrant container
        or IntoChat.OS.ArtCraft.ArtCraftModule;       // its Docker images

    private static Module[] BuiltIn() =>
    [
        new IntoChat.OS.Microsoft.Orleans.OrleansModule(),
        new IntoChat.OS.AI.AIModule(),
        new IntoChat.OS.ArtCraft.ArtCraftModule(),
        new IntoChat.OS.ClickHouse.ClickHouseModule(),
        new IntoChat.OS.Apps.AppsModule(),
        new IntoChat.OS.Flutter.FlutterModule(),
        new IntoChat.OS.Google.Gmail.GmailModule(),
        new IntoChat.OS.Microsoft.Aspire.AspireModule(),
        new IntoChat.OS.Microsoft.Azure.AzureModule(),
        new IntoChat.OS.Microsoft.CSharp.CSharpModule(),
        new IntoChat.OS.Microsoft.GitHub.GitHubModule(),
        new IntoChat.OS.Microsoft.Playwright.PlaywrightModule(),
        new IntoChat.OS.Postgres.PostgresModule(),
        new IntoChat.OS.Qdrant.QdrantModule(),
        new IntoChat.OS.Salesforce.SalesforceModule(),
        new IntoChat.OS.Supabase.SupabaseModule(),
        new IntoChat.OS.Time.TimeModule(),
        new IntoChat.OS.Tools.ToolsModule(),
        new IntoChat.OS.Mcp.McpModule(),
        new IntoChat.OS.Circles.CirclesModule(),
        new IntoChat.OS.Flame.FlameModule(),
    ];

    // Modules the Workshop has added to this OS. Declared here, implemented in
    // OsCatalog.Generated.cs so the composition grows without hand-editing this file.
    private static partial Module[] Generated();

    // The Windows edge: the composition plus desktop automation of the Windows machine the host runs on.
    // Only a host on Windows opts in; the portable composition never carries it.
    public static IReadOnlyList<Module> WithWindowsEdge() => [.. Modules, new IntoChat.OS.Microsoft.Windows.WindowsModule()];

    // Composes the given modules (the portable composition by default) and the OS apps.
    public static IntoChatOSServerBuilder AddOperatingSystem(this IntoChatOSServerBuilder server, IReadOnlyList<Module>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var module in modules ?? Modules) { server.AddModule(module); }
        // The OS is the composition plus its apps; a distribution cannot forget them.
        server.ConfigureServices(services => services.AddOperatingSystemApps());
        return server;
    }
}
