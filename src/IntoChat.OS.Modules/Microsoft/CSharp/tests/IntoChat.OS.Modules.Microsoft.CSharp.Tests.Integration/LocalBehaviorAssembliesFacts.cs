using System.Net;
using System.Security.Cryptography;
using IntoChat.OS.Modules.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class LocalBehaviorAssembliesFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingAnExistingHashDoesNotReplaceFilesHeldByADownloader(bool symbols)
    {
        var ct = TestContext.Current.CancellationToken;
        using var home = TestHome.Create();
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = Store(home, services);
        var compiled = Behavior();
        var root = Directory.CreateDirectory(Path.Combine(home.Path, "behaviors")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(root, compiled.Hash), compiled.Assembly, ct);
        var path = Path.Combine(root, compiled.Hash + (symbols ? ".pdb" : ""));
        if (symbols) { await File.WriteAllBytesAsync(path, compiled.Symbols, ct); }
        var published = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, published);
        using var download = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        await store.Put(compiled, ct);

        Assert.True(await store.Exists(compiled.Hash, ct));
        Assert.Equal(published, File.GetLastWriteTimeUtc(path));
        Assert.Equal(compiled.Assembly, await File.ReadAllBytesAsync(Path.Combine(root, compiled.Hash), ct));
        Assert.Equal(compiled.Symbols, await File.ReadAllBytesAsync(Path.Combine(root, compiled.Hash + ".pdb"), ct));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentPublicationOfOneHashKeepsOneCompletePair()
    {
        var ct = TestContext.Current.CancellationToken;
        using var home = TestHome.Create();
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = Store(home, services);
        var compiled = Behavior();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, 16).Select(async _ => { await start.Task; await store.Put(compiled, ct); }).ToArray();

        start.SetResult();
        await Task.WhenAll(writers);

        var root = Path.Combine(home.Path, "behaviors");
        Assert.True(await store.Exists(compiled.Hash, ct));
        Assert.Equal(compiled.Assembly, await File.ReadAllBytesAsync(Path.Combine(root, compiled.Hash), ct));
        Assert.Equal(compiled.Symbols, await File.ReadAllBytesAsync(Path.Combine(root, compiled.Hash + ".pdb"), ct));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingContentAtTheSameHashCannotBeSilentlyReplaced(bool symbols)
    {
        var ct = TestContext.Current.CancellationToken;
        using var home = TestHome.Create();
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = Store(home, services);
        var compiled = Behavior();
        var root = Directory.CreateDirectory(Path.Combine(home.Path, "behaviors")).FullName;
        var path = Path.Combine(root, compiled.Hash + (symbols ? ".pdb" : ""));
        byte[] original = [9, 8, 7];
        await File.WriteAllBytesAsync(path, original, ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.Put(compiled, ct));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, ct));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private static LocalBehaviorAssemblies Store(TestHome home, IServiceProvider services)
        => new(new ConfigurationBuilder().AddInMemoryCollection([home.Setting]).Build(), new EphemeralDataProtectionProvider(), services, Options.Create(new CSharpOptions()));

    private static CompiledBehavior Behavior()
    {
        byte[] assembly = [1, 2, 3, 4];
        return new(assembly, [5, 6], Convert.ToHexStringLower(SHA256.HashData(assembly)));
    }

    [Fact]
    public async Task TildeHomeUsesTheSharedResolvedStateDirectory()
    {
        using var home = TestHome.Create();
        var directory = Directory.CreateDirectory(Path.Combine(home.Path, "behaviors"));
        var hash = new string('a', 64);
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, hash), [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, hash + ".pdb"), [2], TestContext.Current.CancellationToken);
        var tildeConfiguration = new ConfigurationBuilder().AddInMemoryCollection([new(home.Setting.Key, "~")]).Build();
        var tildeRoot = DigitalBrain.Runtime.Orleans.StateHome.Resolve(tildeConfiguration).Path;
        var configuredHome = "~/" + Path.GetRelativePath(tildeRoot, home.Path);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(home.Setting.Key, configuredHome)]).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new LocalBehaviorAssemblies(configuration, new EphemeralDataProtectionProvider(), services, Options.Create(new CSharpOptions()));

        Assert.True(await store.Exists(hash, TestContext.Current.CancellationToken));
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
