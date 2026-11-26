using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class EdgeGrantsCacheFacts
{
    [Fact]
    public async Task EdgeGrantsCacheCachesAndInvalidates()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "cache-test-brain";
        cache.Invalidate(brain);

        var grant = new Grant
        {
            Id = "grant-cached",
            Owner = "app:local/apps/owner",
            Grantee = "app:local/apps/friend",
            Target = "cache-target",
            Scope = "*",
            WorkspaceId = brain,
            AppId = "local/apps/friend",
            SemanticTypeId = "*",
            TargetNeuronKey = "cache-target",
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        cache.AddGrant(brain, grant);
        var list = await cache.GetGrantsAsync(brain, ct);
        Assert.Contains(list, g => g.Id == "grant-cached");

        cache.Invalidate(brain);
    }

    [Fact]
    public async Task IsConnectionBoundAsyncValidatesBindings()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = EdgeGrantsCache.Current;
        var brain = "binding-test-brain";
        cache.Invalidate(brain);

        var caller = IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/friend-app", new Actor.Person("user", "user"), new BrainId(brain));
        var connectionKey = $"{brain}/connection/custom/target";

        var grant = new Grant
        {
            Id = $"binding:{connectionKey}:local/apps/friend-app",
            Owner = $"brain:{brain}",
            Grantee = "app:local/apps/friend-app",
            Target = connectionKey,
            Scope = "use-via:local/apps/friend-app",
            WorkspaceId = brain,
            AppId = "local/apps/friend-app",
            SemanticTypeId = "use-via:local/apps/friend-app",
            TargetNeuronKey = connectionKey,
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        cache.AddGrant(brain, grant);

        var isBound = await cache.IsConnectionBoundAsync(brain, caller, connectionKey, ct);
        Assert.True(isBound);

        var strangerCaller = IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/stranger", new Actor.Person("user", "user"), new BrainId(brain));
        var strangerBound = await cache.IsConnectionBoundAsync(brain, strangerCaller, connectionKey, ct);
        Assert.False(strangerBound);
    }

    [Fact]
    public void EdgeGrantsCacheExposesAsyncOnlyConnectionBindingChecks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var sln = System.Xml.Linq.XDocument.Load(Path.Combine(dir.FullName, "Intochat.OS.slnx"));
        var rel = sln.Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .First(p => p is not null && p.EndsWith("IntoChat.OS.Modules.Identity.csproj", StringComparison.OrdinalIgnoreCase))!;
        var frameworkDir = Path.GetDirectoryName(Path.Combine(dir.FullName, rel))!;
        var src = File.ReadAllText(Path.Combine(frameworkDir, "Grants", "EdgeGrantsCache.cs"));
        Assert.DoesNotContain("GetAwaiter().GetResult()", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractBrainReturnsFalseWhenTargetHasNoBrainPrefix()
    {
        Assert.False(EdgeGrantsCache.TryExtractBrain("standalone-key", out _));
        Assert.True(EdgeGrantsCache.TryExtractBrain("owner/apps/notes", out var appBrain));
        Assert.Equal(new DigitalBrain.BrainId("owner"), appBrain);
        Assert.True(EdgeGrantsCache.TryExtractBrain("postgres/owner/connection/postgres/main", out var neuronBrain));
        Assert.Equal(new DigitalBrain.BrainId("owner"), neuronBrain);
    }

    [Fact]
    public async Task ExpansionIsTheCallerAlone()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var cache = new EdgeGrantsCache(
            services: services,
            resolveNeuron: (_, _) => throw new InvalidOperationException("ExpandAsync must not resolve any neuron."));
        var caller = IntoChat.OS.Tests.TestEdge.Authority.App("vlad/apps/notes", new Actor.Person("vlad", "vlad"), new BrainId("vlad"));

        Assert.Equal(PrincipalSet.For(caller).Principals, (await cache.ExpandAsync("vlad", caller)).Principals);
    }

    [Fact]
    public async Task NonConnectionTargetsDoNotResolveGrantsNeuron()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var cache = new EdgeGrantsCache(
            services: services,
            resolveNeuron: (_, id) => throw new InvalidOperationException($"Must not resolve phantom neuron '{id}' for a non-connection target."));
        var caller = IntoChat.OS.Tests.TestEdge.Authority.App("owner/apps/notes", new Actor.Person("owner", "owner"), new BrainId("owner"));

        Assert.False(await cache.IsConnectionBoundAsync(caller, "brain/owner/main"));
        Assert.False(await cache.IsConnectionBoundAsync(caller, "grants.default/owner/grants/default"));
        Assert.False(await cache.IsConnectionBoundAsync("owner", caller, "brain/owner/main", TestContext.Current.CancellationToken));
        Assert.False(cache.IsConnectionBound(caller, "brain/owner/main"));
    }
}
