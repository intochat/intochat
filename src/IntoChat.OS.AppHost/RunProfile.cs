using DigitalBrain.Modules.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
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
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Qdrant;
using IntoChat.OS.Modules.Supabase;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.AppHost;

public sealed record BrainProfile
{
    public required OrleansOptions Orleans { get; init; }
    public required PostgresModuleOptions Postgres { get; init; }
    public required QdrantModuleOptions Qdrant { get; init; }
    public required ClickHouseModuleOptions ClickHouse { get; init; }
    public required SupabaseModuleOptions Supabase { get; init; }
    public required AIOptions Ai { get; init; }
    // The lean local run: no Postgres, Qdrant, ClickHouse, Supabase, GitHub, ArtCraft or Playwright hosting at all.
    public bool Lean { get; init; }
}

public static class RunProfile
{
    static RunProfile() => ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
    public static BrainProfile Local => Base() with { Ai = LocalAi() };
    public static string DefaultLocalModel => new Qwen35().Id;

    // The lean local run: Ollama runs on the host (endpoint only, no container), the default model is qwen3.5:4b unless
    // the launcher passed IntoChatOS:Local:Model, and the C# sandbox is the one container.
    public static BrainProfile LocalLean => LocalLeanWith(null);
    public static BrainProfile Cloud => Base() with { Ai = CloudAi() };
    // Only OpenAI is redirected to a local endpoint, so the default chat model must be an OpenAI one: the
    // cloud default (OpenRouter) has no key here and every agent turn without a Model would fail.
    public static BrainProfile Reference => Base() with
    {
        Ai = CloudAi().WithDefaultLlm<IGpt56Luna>().WithoutWebSearch().WithModelEndpoint(AiProvider.OpenAI, new Uri("http://127.0.0.1:1/v1/")),
    };

    public static BrainProfile LocalLeanWith(string? model)
    {
        var ai = new AIOptions().WithModelEndpoint(AiProvider.Ollama, new Uri(LocalOllamaEndpoint)).WithoutWebSearch();
        ai.Default.Model = nameof(IQwen35);
        if (model is { Length: > 0 }) { ai.Ollama.Models[nameof(IQwen35)] = new AIModelOptions { Model = model }; }
        return Base() with { Ai = ai, Lean = true };
    }

    // The modules a profile switched off (IntoChatOS:Modules:<id>:Enabled=false). The AppHost's configuration never reaches the
    // kernel on its own, so each one is projected onto the kernel and MCP resources as an environment variable.
    public static IReadOnlyList<string> DisabledModuleKeys(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return [.. configuration.GetSection(ModuleConfiguration.ModulesSectionName).GetChildren()
            .Select(module => ModuleConfiguration.ModuleSection(module.Key) + ":Enabled")
            .Where(key => configuration.GetValue<bool?>(key) == false)
            .Order(StringComparer.Ordinal)];
    }

    public static IResourceBuilder<T> WithDisabledModules<T>(this IResourceBuilder<T> resource, IConfiguration configuration) where T : IResourceWithEnvironment
    {
        foreach (var key in DisabledModuleKeys(configuration)) { resource.WithEnvironment(ModuleConfiguration.EnvironmentName(key), "false"); }
        return resource;
    }

    public const string LocalOllamaEndpoint = IntoChatOSNames.LocalOllamaEndpointKey;

    // The modules the local and local+cloud runs switch off by default (code, not a configuration file); an explicit
    // IntoChatOS:Modules:<id>:Enabled from appsettings, the environment or the command line still wins.
    public static IReadOnlyList<string> LocalDisabledModules => IntoChatOSNames.LocalDisabledModules;

    // The cloud run keeps every module on by default.
    public static IReadOnlyList<string> CloudDisabledModules { get; } = [];

    public static bool IsCloud(string topology) => string.Equals(topology, IntoChatOSNames.TopologyCloud, StringComparison.OrdinalIgnoreCase);

    // IntoChatOS:Topology (unset = local) selects the brain: local and local+cloud are the lean host-Ollama run, cloud is
    // the product's Cloud brain. An unknown value stops the run, naming the key and the known values, never the value.
    public static string Topology(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var topology = configuration[IntoChatOSNames.TopologyKey];
        if (string.IsNullOrWhiteSpace(topology)) { return IntoChatOSNames.TopologyLocal; }
        // The earlier spelling resolves to the canonical value (R4-09).
        if (string.Equals(topology.Trim(), IntoChatOSNames.TopologyLocalCloudAlias, StringComparison.OrdinalIgnoreCase)) { return IntoChatOSNames.TopologyLocalCloud; }
        foreach (var known in new[] { IntoChatOSNames.TopologyLocal, IntoChatOSNames.TopologyLocalCloud, IntoChatOSNames.TopologyCloud })
        {
            if (string.Equals(topology.Trim(), known, StringComparison.OrdinalIgnoreCase)) { return known; }
        }
        throw new InvalidOperationException(
            $"{IntoChatOSNames.TopologyKey} must be one of {IntoChatOSNames.TopologyLocal}, {IntoChatOSNames.TopologyLocalCloud} or {IntoChatOSNames.TopologyCloud}.");
    }

    public static BrainProfile ForTopology(string topology, string? localModel = null)
        => IsCloud(topology) ? Cloud : LocalLeanWith(localModel);

    // The module defaults of a topology as configuration entries (IntoChatOS:Modules:<id>:Enabled=false).
    public static IEnumerable<KeyValuePair<string, string?>> ModuleDefaults(string topology)
        => (IsCloud(topology) ? CloudDisabledModules : LocalDisabledModules)
            .Select(module => new KeyValuePair<string, string?>(ModuleConfiguration.ModuleSection(module) + ":Enabled", "false"));

    public static void CopyOnto(AIOptions target, AIOptions source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Default = source.Default;
        target.Telemetry = source.Telemetry;
        target.OpenAI = source.OpenAI;
        target.Anthropic = source.Anthropic;
        target.Google = source.Google;
        target.XAI = source.XAI;
        target.OpenRouter = source.OpenRouter;
        target.Ollama = source.Ollama;
        target.Tavily = source.Tavily;
        target.SystemOne = source.SystemOne;
        target.Hosting = source.Hosting;
        target.ModelProfiles.Clear();
        foreach (var (key, value) in source.ModelProfiles)
        { target.ModelProfiles[key] = value; }
    }

    public static IntoChatOSBuilder Apply(IntoChatOSBuilder brain, BrainProfile profile)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(profile);
        brain = brain.WithHosting<OrleansModuleHosting, OrleansOptions>(profile.Orleans);
        if (!profile.Lean)
        {
            brain = brain
                .WithModule<PostgresModule, PostgresModuleOptions>(profile.Postgres)
                .WithModule<QdrantModule, QdrantModuleOptions>(profile.Qdrant)
                .WithModule<ClickHouseModule, ClickHouseModuleOptions>(profile.ClickHouse)
                .WithModule<SupabaseModule, SupabaseModuleOptions>(profile.Supabase);
        }
        brain = brain.WithHosting<AIModuleHosting, AIOptions>(profile.Ai);
        if (!profile.Lean) { brain = brain.WithModule<GitHubModule>(); }
        brain = brain.WithHosting<AspireModuleHosting>().WithHosting<CSharpModuleHosting>();
        return profile.Lean
            ? brain
            : brain
                .WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps())
                .WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins);
    }

    private static BrainProfile Base() => new()
    {
        // Azurite is not part of the module selection: the AppHost adds it only for IntoChatOS:Topology=cloud.
        Orleans = new OrleansOptions(),
        Postgres = new PostgresModuleOptions().WithPostgres(options => options.DatabaseName = "intochat_os"),
        Qdrant = new QdrantModuleOptions().WithHostedQdrant(),
        ClickHouse = new ClickHouseModuleOptions().WithClickHouse(),
        Supabase = new SupabaseModuleOptions().WithConnection("supabase"),
        Ai = new AIOptions(),
    };

    private static AIOptions LocalAi() => new AIOptions()
        .WithDefaultLlm<IQwen35>()
        .WithDefaultEmbedding<IEmbeddingGemma>()
        .WithVoiceToText<IWhisperSmall>()
        .WithDefaultSystemOne<ILocalJev>()
        .WithoutWebSearch();

    private static AIOptions CloudAi() => new AIOptions()
        .WithLlm<IGpt56Luna>()
        .WithDefaultLlm<IDeepSeekV41Flash>()
        .WithDefaultEmbedding<ITextEmbedding3Small>()
        .WithVoiceToText<IWhisperLargeV3Turbo>()
        .WithoutLocalModels()
        .WithTavilySearch();
}

public static class AppHostIntoChatOS
{
    public static IntoChatOSBuilder Add(IDistributedApplicationBuilder builder, string name, BrainProfile profile, IntoChatOSHostingOptions? hosting = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RunProfile.Apply(builder.AddIntoChatOS(name, hosting), profile);
    }
}
