using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Aspire.Hosting;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;
using IntoChat.OS.Modules.ClickHouse;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting;

public static class DeploymentChain
{
    public static IntoChatOSBuilder WithAI(this IntoChatOSBuilder os, Action<AIDeployment> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var profile = RequireProfile(os, nameof(WithAI));
        var deployment = new AIDeployment();
        configure(deployment);

        var plan = deployment.Resolve(profile, os.ApplicationBuilder.Configuration);
        os = os.WithHosting<AIModuleHosting, AIOptions>(plan.Ai);
        if (plan.Devices is not null && profile.IsEnabled("devices"))
        {
            if (plan.Devices.Ollama.Enabled
                && string.Equals(plan.Devices.Ollama.Endpoint, IntoChat.OS.Host.Configuration.IntoChatOSNames.LocalOllamaEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                AIHostingExtensions.EnsureAutoOllamaContainer(os, plan.Devices.Ollama.Endpoint);
            }
            os = os.WithModule<DevicesModule, DevicesOptions>(plan.Devices);
        }
        else if (plan.Devices is null)
        {
            // No local device runtimes declared (e.g. local host + cloud AI): suppress auto-starting
            // Ollama, FoundryLocal, and SherpaOnnx/Silero unless explicitly overridden in configuration.
            var devicesOptions = ModuleConfiguration.ModuleOptionsSection("devices");
            os.SetDefaultSetting($"{devicesOptions}:Ollama:Enabled", "false");
            os.SetDefaultSetting($"{devicesOptions}:FoundryLocal:Enabled", "false");
            os.SetDefaultSetting($"{devicesOptions}:SherpaOnnx:Enabled", "false");
        }

        if (plan.Web is not null && profile.IsEnabled("webtools"))
        {
            os = os.WithHosting<WebModuleHosting, WebModuleOptions>(plan.Web);
        }

        if (plan.Mcp is not null && profile.IsEnabled("mcp"))
        {
            os = os.WithModule<McpModule, McpModuleOptions>(plan.Mcp);
        }

        return os;
    }

    public static IntoChatOSBuilder WithInfrastructure(this IntoChatOSBuilder os, Action<InfrastructureBackingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var profile = RequireProfile(os, nameof(WithInfrastructure));
        var builder = new InfrastructureBackingBuilder();
        configure(builder);
        return builder.ApplyTo(os, profile);
    }

    [Obsolete("Use WithInfrastructure(infra => infra.Postgres(...)) instead.")]
    public static IntoChatOSBuilder WithPostgres(this IntoChatOSBuilder os, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var profile = RequireProfile(os, nameof(WithPostgres));
        return profile.IsEnabled("postgres")
            ? os.WithModule<PostgresModule, PostgresModuleOptions>(new PostgresModuleOptions().WithPostgres(options => options.DatabaseName = databaseName))
            : os;
    }

    [Obsolete("Use WithInfrastructure(infra => infra.ClickHouse(...)) instead.")]
    public static IntoChatOSBuilder WithClickHouse(this IntoChatOSBuilder os)
    {
        var profile = RequireProfile(os, nameof(WithClickHouse));
        return profile.IsEnabled("clickhouse")
            ? os.WithModule<ClickHouseModule, ClickHouseModuleOptions>(new ClickHouseModuleOptions().WithClickHouse())
            : os;
    }

    [Obsolete("GitHub is auto-discovered by OperatingSystemModules; no AppHost composition call is required.")]
    public static IntoChatOSBuilder WithGitHub(this IntoChatOSBuilder os)
    {
        var profile = RequireProfile(os, nameof(WithGitHub));
        return profile.IsEnabled("github")
            ? os.WithModule<GitHubModule>()
            : os;
    }

    [Obsolete("Use WithInfrastructure(infra => infra.Playwright(...)) instead.")]
    public static IntoChatOSBuilder WithPlaywright(this IntoChatOSBuilder os)
    {
        var profile = RequireProfile(os, nameof(WithPlaywright));
        return profile.IsEnabled("playwright")
            ? os.WithModule<PlaywrightModule, PlaywrightModuleOptions>(playwright => playwright.AppOrigins = ArtCraftModuleOptions.HostedOrigins)
            : os;
    }

    [Obsolete("Use WithInfrastructure(infra => infra.ArtCraft(...)) instead.")]
    public static IntoChatOSBuilder WithArtCraft(this IntoChatOSBuilder os)
    {
        var profile = RequireProfile(os, nameof(WithArtCraft));
        return profile.IsEnabled("artcraft")
            ? os.WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(artcraft => artcraft.WithHostedApps())
            : os;
    }

    [Obsolete("Use WithInfrastructure(infra => infra.Sandbox(...)) instead.")]
    public static IntoChatOSBuilder WithSandbox(this IntoChatOSBuilder os)
    {
        var profile = RequireProfile(os, nameof(WithSandbox));
        return profile.IsEnabled(SandboxModuleHosting.ModuleName)
            ? os.WithHosting<SandboxModuleHosting>()
            : os;
    }

    private static DeploymentProfile RequireProfile(IntoChatOSBuilder os, string member)
    {
        ArgumentNullException.ThrowIfNull(os);
        return os.Profile ?? throw new InvalidOperationException($"{member} needs the deployment from builder.AddIntoChatOS(name).");
    }
}
