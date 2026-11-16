using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;

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

        var caller = CapabilityCallers.Of(AppPrincipal.Id("local/apps/friend-app"), "user", brain, CallerKind.App, "local/apps/friend-app");
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

        var strangerCaller = CapabilityCallers.Of(AppPrincipal.Id("local/apps/stranger"), "user", brain, CallerKind.App, "local/apps/stranger");
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
            .First(p => p is not null && p.EndsWith("IntoChat.OS.Framework.csproj", StringComparison.OrdinalIgnoreCase))!;
        var frameworkDir = Path.GetDirectoryName(Path.Combine(dir.FullName, rel))!;
        var src = File.ReadAllText(Path.Combine(frameworkDir, "Authority", "Identity", "Grants", "EdgeGrantsCache.cs"));
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
}
