using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class WebModuleHostingFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocalTopologyAddsSearxngAndProjectsItsUrl()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal;
        var profile = DeploymentProfile.From(builder);

        Assert.Equal(SearchProvider.Searxng, profile.Web.Provider);

        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions());
        brain = profile.Apply(brain);

        var searxng = Assert.Single(
            builder.Resources.OfType<ContainerResource>(),
            static r => r.Name.EndsWith(WebModuleHosting.SearxngResourceName, StringComparison.Ordinal));

        var endpoint = Assert.Single(searxng.Annotations.OfType<EndpointAnnotation>(), static e => e.Name == "http");
        Assert.Equal(8080, endpoint.TargetPort);

        var mount = Assert.Single(searxng.Annotations.OfType<ContainerMountAnnotation>(), static m => m.Target == "/etc/searxng");
        Assert.True(mount.IsReadOnly);

        var lifetime = Assert.Single(searxng.Annotations.OfType<ContainerLifetimeAnnotation>());
        Assert.Equal(ContainerLifetime.Persistent, lifetime.Lifetime);

        var parent = Assert.Single(searxng.Annotations.OfType<ResourceRelationshipAnnotation>(), static r => r.Type == "Parent");
        Assert.Contains("webtools", parent.Resource.Name, StringComparison.Ordinal);

        var searxngEnv = new Dictionary<string, object>();
        var searxngCtx = new EnvironmentCallbackContext(builder.ExecutionContext, searxng, searxngEnv, Ct);
        foreach (var cb in searxng.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await cb.Callback(searxngCtx);
        }

        Assert.True(searxngEnv.ContainsKey("SEARXNG_SECRET"));

        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        var siloEnv = new Dictionary<string, object>();
        var siloCtx = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, siloEnv, Ct);
        foreach (var cb in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await cb.Callback(siloCtx);
        }

        Assert.Equal("Searxng", siloEnv["IntoChatOS__Modules__webtools__Options__Provider"]?.ToString());
        Assert.True(siloEnv.TryGetValue("IntoChatOS__Modules__webtools__Options__SearxngUrl", out var projectedUrl));
        Assert.IsType<EndpointReference>(projectedUrl);
        Assert.Same(searxng, ((EndpointReference)projectedUrl!).Resource);
    }

    [Fact]
    public async Task CloudTopologyUsesBraveAndAddsNoContainer()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyCloud;
        var profile = DeploymentProfile.From(builder);

        Assert.Equal(SearchProvider.Brave, profile.Web.Provider);

        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions());
        brain = profile.Apply(brain);

        Assert.DoesNotContain(
            builder.Resources.OfType<ContainerResource>(),
            static r => r.Name.Contains(WebModuleHosting.SearxngResourceName, StringComparison.OrdinalIgnoreCase));

        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        var siloEnv = new Dictionary<string, object>();
        var siloCtx = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, siloEnv, Ct);
        foreach (var cb in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await cb.Callback(siloCtx);
        }

        Assert.Equal("Brave", siloEnv["IntoChatOS__Modules__webtools__Options__Provider"]?.ToString());
        Assert.DoesNotContain("IntoChatOS__Modules__webtools__Options__SearxngUrl", siloEnv.Keys);
    }

    [Fact]
    public void SearxngConfigRemovesEveryGoogleEngine()
    {
        var configDir = WebModuleHosting.ResolveSearxngConfigDirectory();
        var settingsPath = Path.Combine(configDir, "settings.yml");
        Assert.True(File.Exists(settingsPath), $"Missing SearXNG settings file at {settingsPath}");

        var yaml = File.ReadAllText(settingsPath);
        string[] requiredRemoved =
        [
            "google",
            "google images",
            "google news",
            "google videos",
            "google scholar",
            "google play apps",
        ];

        // Parse the engines.remove block and confirm every Google engine is listed under remove.
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inRemove = false;
        var removed = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "remove:")
            {
                inRemove = true;
                continue;
            }

            if (inRemove)
            {
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    removed.Add(trimmed[2..].Trim());
                }
                else if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    inRemove = false;
                }
            }
        }

        foreach (var engine in requiredRemoved)
        {
            Assert.Contains(engine, removed);
        }

        // Confirm no Google engine name appears outside the remove block.
        var googleLines = lines.Where(static l => l.Contains("google", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(removed.Count(static e => e.Contains("google", StringComparison.OrdinalIgnoreCase)), googleLines.Length);
        Assert.Contains("- json", yaml, StringComparison.Ordinal);
        Assert.Contains("limiter: false", yaml, StringComparison.Ordinal);
        Assert.Contains("image_proxy: false", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImageIsPinned()
    {
        Assert.Equal("docker.io/searxng/searxng", WebModuleHosting.SearxngImage);
        Assert.False(string.IsNullOrWhiteSpace(WebModuleHosting.SearxngImageTag));
        Assert.NotEqual("latest", WebModuleHosting.SearxngImageTag, StringComparer.OrdinalIgnoreCase);
        Assert.Matches(@"^\d{4}\.\d{1,2}\.\d{1,2}-[0-9a-f]+$", WebModuleHosting.SearxngImageTag);
    }
}
