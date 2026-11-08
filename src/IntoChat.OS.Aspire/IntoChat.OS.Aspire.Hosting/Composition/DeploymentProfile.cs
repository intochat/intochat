using Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.FoundryLocal;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Modules.AI.Models.OpenRouter;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;
using IntoChat.OS.Modules.ClickHouse;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Qdrant;
using IntoChat.OS.Modules.Supabase;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// One developer deployment of the OS, resolved once from configuration: the topology (IntoChatOS:Topology, unset = local)
// picks the models and storage; module enablement (IntoChatOS:Modules:<id>:Enabled, defaulted by LocalRunDefaults) picks
// which modules get hosting. The kernel and MCP apply the same defaults themselves, so only explicit overrides are projected.
public sealed record DeploymentProfile
{
    public const string UiPortKey = "IntoChatOS:Http:UiPort";
    public const string McpPortKey = "IntoChatOS:Http:McpPort";
    public const string ConfigFileKey = "IntoChatOS:ConfigFile";

    public required string Topology { get; init; }
    public bool Cloud => Topology == IntoChatOSNames.TopologyCloud;
    public required string Home { get; init; }
    public int UiPort { get; init; } = 5080;
    public int McpPort { get; init; } = 5081;
    public bool ShellEnabled { get; init; } = true;
    public bool FrontEnabled { get; init; } = true;
    public required IReadOnlyList<string> ModuleOverrides { get; init; }
    public required OrleansOptions Orleans { get; init; }
    public required AIOptions Ai { get; init; }
    private IConfiguration Configuration { get; init; } = null!;

    public bool IsEnabled(string moduleId) => LocalRunDefaults.IsEnabled(Configuration, moduleId);

    // Loads IntoChatOS:ConfigFile and the topology's module defaults into the builder's configuration, then resolves the profile.
    public static DeploymentProfile From(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        if (configuration[ConfigFileKey] is { Length: > 0 } configFile) { configuration.AddJsonFile(configFile, optional: false); }
        var topology = IntoChatOSNames.CanonicalTopology(configuration[IntoChatOSNames.TopologyKey])
            ?? throw new InvalidOperationException(
                $"{IntoChatOSNames.TopologyKey} must be one of {IntoChatOSNames.TopologyLocal}, {IntoChatOSNames.TopologyLocalCloud} or {IntoChatOSNames.TopologyCloud}.");
        var overrides = ExplicitModuleSettings(configuration, topology);
        LocalRunDefaults.Apply(configuration);

        var cloud = topology == IntoChatOSNames.TopologyCloud;
        var orleans = new OrleansOptions();
        // Azurite carries grain state and the cookie key ring on cloud; a local run keeps both on the machine.
        if (cloud) { orleans.WithAzurite(); }
        return new()
        {
            Topology = topology,
            Home = IntoChatOSHome.Resolve(configuration),
            UiPort = configuration.GetValue(UiPortKey, 5080),
            McpPort = configuration.GetValue(McpPortKey, 5081),
            ShellEnabled = configuration.GetValue(IntoChatOSNames.ShellEnabledKey, true),
            FrontEnabled = configuration.GetValue(IntoChatOSNames.FrontEnabledKey, true),
            ModuleOverrides = overrides,
            Orleans = orleans,
            Ai = cloud ? CloudAi() : LocalAi(configuration[IntoChatOSHostingOptions.LocalModelKey]),
            Configuration = configuration,
        };
    }

    // The Enabled settings the AppHost's own configuration states that differ from the topology default: the kernel and MCP
    // apply the default themselves, so these are all they need to hear.
    internal static IReadOnlyList<string> ExplicitModuleSettings(IConfiguration configuration, string topology)
    {
        var defaults = LocalRunDefaults.For(topology);
        return [.. configuration.GetSection(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModulesSectionName).GetChildren()
            .Select(module => LocalRunDefaults.EnabledKey(module.Key))
            .Where(key => configuration[key] is { Length: > 0 } value
                && !string.Equals(value, defaults.GetValueOrDefault(key) ?? "true", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }

    // Adds the hosting of every enabled module: a module switched off (by the topology default or explicitly) gets no
    // containers, parameters or projections.
    internal IntoChatOSBuilder Apply(IntoChatOSBuilder os)
    {
        os = os.WithHosting<OrleansModuleHosting, OrleansOptions>(Orleans);
        if (IsEnabled("postgres")) { os = os.WithModule<PostgresModule, PostgresModuleOptions>(new PostgresModuleOptions().WithPostgres(options => options.DatabaseName = "intochat_os")); }
        if (IsEnabled("qdrant")) { os = os.WithModule<QdrantModule, QdrantModuleOptions>(new QdrantModuleOptions().WithHostedQdrant()); }
        if (IsEnabled("clickhouse")) { os = os.WithModule<ClickHouseModule, ClickHouseModuleOptions>(new ClickHouseModuleOptions().WithClickHouse()); }
        if (IsEnabled("supabase")) { os = os.WithModule<SupabaseModule, SupabaseModuleOptions>(new SupabaseModuleOptions().WithConnection("supabase")); }
        os = os.WithHosting<AIModuleHosting, AIOptions>(Ai);
        if (IsEnabled("github")) { os = os.WithModule<GitHubModule>(); }
        os = os.WithHosting<AspireModuleHosting>();
        if (IsEnabled("artcraft")) { os = os.WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps()); }
        // The shell's PhotoCraft and FilmCraft apps still open their studios; a cloud deployment serves them from the kernel image.
        else if (!Cloud) { ArtCraftModuleHosting.AddStudios(os); }
        if (IsEnabled("playwright")) { os = os.WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins); }
        return os;
    }

    // Local: Ollama on the host (endpoint only, no container) serving qwen3.5:4b unless the launcher passed IntoChatOS:Local:Model.
    private static AIOptions LocalAi(string? model)
    {
        var ai = new AIOptions().WithModelEndpoint(AiProvider.Ollama, new Uri(IntoChatOSNames.LocalOllamaEndpoint)).WithoutWebSearch();
        ai.Default.Model = nameof(IQwen35);
        if (model is { Length: > 0 }) { ai.Ollama.Models[nameof(IQwen35)] = new AIModelOptions { Model = model }; }
        return ai;
    }

    private static AIOptions CloudAi() => new AIOptions()
        .WithLlm<IGpt56Luna>()
        .WithDefaultLlm<IDeepSeekV41Flash>()
        .WithDefaultEmbedding<ITextEmbedding3Small>()
        .WithVoiceToText<IWhisperLargeV3Turbo>()
        .WithoutLocalModels()
        .WithTavilySearch();
}

// Where local state lives: IntoChatOS:Home, or ~/.intochat.
public static class IntoChatOSHome
{
    public static string Resolve(IConfiguration configuration) => DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration,
        new DigitalBrain.Runtime.Orleans.RuntimeOptions { HomeKey = IntoChatOSNames.HomeKey, DefaultHomeDirectoryName = ".intochat" }).Path;
}
