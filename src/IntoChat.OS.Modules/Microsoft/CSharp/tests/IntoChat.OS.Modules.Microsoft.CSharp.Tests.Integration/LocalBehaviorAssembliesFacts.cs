using System.Net;
using IntoChat.OS.Modules.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class LocalBehaviorAssembliesFacts
{
    [Fact]
    public async Task TildeHomeUsesTheSharedResolvedStateDirectory()
    {
        var home = Directory.CreateTempSubdirectory("intochat-local-assemblies-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(home.FullName, "behaviors"));
            var hash = new string('a', 64);
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, hash), [1], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, hash + ".pdb"), [2], TestContext.Current.CancellationToken);
            var configuredHome = "~/" + Path.GetRelativePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), home.FullName);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntoChatOS:Home"] = configuredHome }).Build();
            using var services = new ServiceCollection().BuildServiceProvider();
            var store = new LocalBehaviorAssemblies(configuration, new EphemeralDataProtectionProvider(), services, Options.Create(new CSharpOptions()));

            Assert.True(await store.Exists(hash, TestContext.Current.CancellationToken));
        }
        finally { home.Delete(recursive: true); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task LocalSandboxDownloadsAssembliesAndSymbolsOverHttpAndKeepsThemAfterRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<CSharpModule, CSharpOptions>(options => options.SandboxUrl = "http://127.0.0.1:1").StartAsync(ct);
        var store = brain.Services.GetRequiredService<IBehaviorAssemblies>();
        var compiled = new CSharpScriptCheck().Compile(new Dictionary<string, string> { ["local.cs"] = "Console.WriteLine(7);\n" }).Assemblies["local.cs"];
        await store.Put(compiled, ct);

        var assembly = await store.ReadUrl(compiled.Hash, TimeSpan.FromMinutes(1), ct);
        Assert.Equal("http", assembly.Scheme);
        Assert.Equal("host.docker.internal", assembly.Host);
        Assert.Equal(compiled.Assembly, await brain.HttpClient.GetByteArrayAsync(OnHost(brain, assembly), ct));
        var symbols = await store.ReadUrl(compiled.Hash + ".pdb", TimeSpan.FromMinutes(1), ct);
        Assert.Equal(compiled.Symbols, await brain.HttpClient.GetByteArrayAsync(OnHost(brain, symbols), ct));

        await brain.RestartAsync(ct);
        store = brain.Services.GetRequiredService<IBehaviorAssemblies>();
        Assert.True(await store.Exists(compiled.Hash, ct));
        var restored = await store.ReadUrl(compiled.Hash, TimeSpan.FromMinutes(1), ct);
        Assert.Equal(compiled.Assembly, await brain.HttpClient.GetByteArrayAsync(OnHost(brain, restored), ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task LocalDownloadsRejectMissingTokensAndTokensForDifferentFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<CSharpModule, CSharpOptions>(options => options.SandboxUrl = "http://127.0.0.1:1").StartAsync(ct);
        var store = brain.Services.GetRequiredService<IBehaviorAssemblies>();
        var compiled = new CSharpScriptCheck().Compile(new Dictionary<string, string> { ["local.cs"] = "Console.WriteLine(8);\n" }).Assemblies["local.cs"];
        await store.Put(compiled, ct);
        var url = await store.ReadUrl(compiled.Hash, TimeSpan.FromMinutes(1), ct);
        Assert.Equal("http", url.Scheme);
        var unsigned = new UriBuilder(OnHost(brain, url)) { Query = "" }.Uri;
        using var noToken = await brain.HttpClient.GetAsync(unsigned, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        using var wrongFile = await brain.HttpClient.GetAsync(new UriBuilder(OnHost(brain, url)) { Path = url.AbsolutePath + ".pdb" }.Uri, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongFile.StatusCode);
        using var tampered = await brain.HttpClient.GetAsync(new UriBuilder(OnHost(brain, url)) { Query = "token=not-a-token" }.Uri, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);

        var expired = brain.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(LocalBehaviorAssemblies.ProtectionPurpose)
            .ToTimeLimitedDataProtector().Protect(compiled.Hash, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var expiredResponse = await brain.HttpClient.GetAsync(new UriBuilder(OnHost(brain, url)) { Query = "token=" + Uri.EscapeDataString(expired) }.Uri, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadUrl("../outside", TimeSpan.FromMinutes(1), ct));
        using var traversal = await brain.HttpClient.GetAsync("/runtime/v1/assemblies/%2e%2e%2foutside?token=" + Uri.EscapeDataString(expired), ct);
        Assert.Contains(traversal.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
    }

    private static Uri OnHost(ITestBrain brain, Uri url) => new UriBuilder(url) { Host = brain.HttpClient.BaseAddress!.Host }.Uri;
}
