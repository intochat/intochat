using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Modules.Web.Aspire.Hosting;

public sealed class WebModuleHosting : ModuleHosting<WebModule>
{
    public const string SearxngResourceName = "searxng";
    public const string SearxngImage = "docker.io/searxng/searxng";
    public const string SearxngImageTag = "2026.11.14-6ae7c6cb8";

    private static readonly string ProviderEnvironmentKey =
        ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleOptionsSection("webtools") + ":Provider");

    private static readonly string SearxngUrlEnvironmentKey =
        ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleOptionsSection("webtools") + ":SearxngUrl");

    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ProjectSecrets(brain);

        var options = brain.GetModuleConfiguration("webtools").GetModuleOptions<WebModuleOptions>("webtools");
        var module = new IntoChatOSModuleBuilder<WebModuleHosting>(brain);
        var state = module.GetOrAddProjection(b => new WebHostingState(b, module.Resource));
        state.Configure(options);
    }

    public static string ResolveSearxngConfigDirectory()
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "Hosting", "Modules", "Web", "searxng");
        if (File.Exists(Path.Combine(outputDir, "settings.yml")))
        {
            return outputDir;
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "IntoChat.OS.AppHost", "Hosting", "Modules", "Web", "searxng");
            if (File.Exists(Path.Combine(candidate, "settings.yml")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the SearXNG settings.yml directory.");
    }

    private sealed class WebHostingState(
        IntoChatOSBuilder brain,
        IResourceBuilder<IntoChatOSModuleResource> module) : IntoChatOSModuleProjection
    {
        private SearchProvider _provider = SearchProvider.Off;
        private IResourceBuilder<ContainerResource>? _searxng;

        internal void Configure(WebModuleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _provider = options.Provider;

            if (_provider == SearchProvider.Searxng && _searxng is null)
            {
                var secretName = brain.ResourceName("searxng-secret");
                var secret = brain.ApplicationBuilder.ExecutionContext.IsRunMode
                    ? brain.ApplicationBuilder.AddParameter(
                        secretName,
                        new GenerateParameterDefault { MinLength = 32, Special = false },
                        secret: true,
                        persist: true)
                    : brain.ApplicationBuilder.AddParameter(secretName, secret: true);

                _searxng = brain.ApplicationBuilder
                    .AddContainer(brain.ChildName("webtools", SearxngResourceName, brain.ResourceName(SearxngResourceName)), SearxngImage, SearxngImageTag)
                    .WithHttpEndpoint(targetPort: 8080, name: "http")
                    .WithBindMount(ResolveSearxngConfigDirectory(), "/etc/searxng", isReadOnly: true)
                    .WithEnvironment("SEARXNG_SECRET", secret)
                    .WithLifetime(ContainerLifetime.Persistent)
                    .WithLegacyContainerCleanup(brain.ApplicationBuilder)
                    .WithParentRelationship(module);
            }
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.WithEnvironment(ProviderEnvironmentKey, _provider.ToString());
            if (_searxng is not null)
            {
                builder.WithEnvironment(SearxngUrlEnvironmentKey, _searxng.GetEndpoint("http"));
            }
        }
    }
}
