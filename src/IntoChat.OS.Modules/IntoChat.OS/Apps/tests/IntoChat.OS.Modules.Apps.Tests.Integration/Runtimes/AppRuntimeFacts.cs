using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;

// The runtime is the one seam an app deploys through: the whole program, with its configuration,
// every time. A host without a csharp module registers no runtime and apps are data only.
internal sealed class FakeAppRuntime : IAppRuntime
{
    public List<(string AppKey, Dictionary<string, string> Sources, Dictionary<string, string> Configuration)> Deployed { get; } = [];
    public List<string> Retired { get; } = [];
    public Func<Dictionary<string, string>, Exception?>? Refuse { get; set; }

    // Each deployed file's hash is "hash:{path}", so a fact can tell which file a hash names.
    public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
    {
        if (Refuse?.Invoke(sources) is { } error) { throw error; }
        Deployed.Add((appKey, new(sources, StringComparer.Ordinal), new(configuration, StringComparer.Ordinal)));
        return Task.FromResult<IReadOnlyDictionary<string, string>>(sources.Keys.ToDictionary(path => path, path => "hash:" + path, StringComparer.Ordinal));
    }

    public Task Retire(string appKey, CancellationToken cancellationToken) { Retired.Add(appKey); return Task.CompletedTask; }

    public static Task<ITestBrain> Start(FakeAppRuntime runtime, CancellationToken ct)
        => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(runtime).StartAsync(ct);
}

internal static class AppRuntimeHosting
{
    // The app runtime is the script sandbox's seam: without a csharp module there is no sandbox to deploy
    // into, so a recording runtime stands in for it; the app's deploy path is exercised either way.
    public static TestBrain WithFakeAppRuntime(this TestBrain builder, FakeAppRuntime runtime)
        => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAppRuntime>();
            services.AddSingleton<IAppRuntime>(runtime);
        });
}

public sealed class AppRuntimeFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ABuildDeploysTheWholeProgramWithItsConfigurationAndDeleteRetiresIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "tracker"));
        await app.Create(new("Tracker", "tracks", [new("ask", "ask")], [new("Feed", "what to watch", "news")], []), "spec");
        await app.BeginBuild(null);

        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/track.cs"] = "// b", ["lib/Shared.cs"] = "// lib", ["prompts/x.md"] = "not code" }, null);

        var deployment = Assert.Single(runtime.Deployed);
        Assert.Equal(["lib/Shared.cs", "synapses/track.cs"], deployment.Sources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(IApp.Key("runtime-facts", "tracker"), deployment.Configuration["App"]);
        Assert.Equal("news", deployment.Configuration["Feed"]);
        await app.Delete();
        Assert.Equal([IApp.Key("runtime-facts", "tracker")], runtime.Retired);
    }

    // Settings opens the account page and Apps builds share links on the host's public origin; unset is empty.
    [Trait("host", "own")]
    [Theory]
    [InlineData("https://os.example.test/", "https://os.example.test/")]
    [InlineData(null, "")]
    public async Task ScriptsReadTheHostsPublicOrigin(string? configured, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        var builder = TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(runtime);
        if (configured is not null) { builder = builder.WithConfiguration(IntoChatOSConfiguration.ConnectionsPublicOriginKey, configured); }
        await using var brain = await builder.StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "origin"));
        await app.Create(new("Origin", "reads the origin", [new("ask", "ask")], [], []), "spec");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/a.cs"] = "// a" }, null);
        Assert.Equal(expected, Assert.Single(runtime.Deployed).Configuration["PublicOrigin"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task EveryProgramOrSettingChangeRedeploysTheWholeProgram()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "fresh"));
        await app.Create(new("Fresh", "fresh", [], [new("Feed", "", "a")], []), "spec");
        await app.WriteBehavior("synapses/old.cs", "// old", Guid.NewGuid());
        await app.WriteBehavior("synapses/new.cs", "// new", Guid.NewGuid());
        await app.Configure(new() { ["Feed"] = "b" }, []);

        Assert.Equal(3, runtime.Deployed.Count);
        Assert.Equal(["synapses/new.cs", "synapses/old.cs"], runtime.Deployed[1].Sources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("b", runtime.Deployed[2].Configuration["Feed"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AWriteWhoseProgramDoesNotCompileFailsAndKeepsThePreviousFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime { Refuse = sources => sources.ContainsKey("synapses/bad.cs") ? new InvalidOperationException("The program does not compile: synapses/bad.cs line 1 CS0029") : null };
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "strict"));
        await app.Create(new("Strict", "", [], [], []), "spec");
        await app.WriteBehavior("synapses/good.cs", "// ok", Guid.NewGuid());

        // One reaction, so it passes the shape and reaches the runtime's compiler, which refuses it.
        const string bad = "await foreach (var request in brain.On(app, \"ask\", brain.Stopping)) { int x = \"no\"; }";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.WriteBehavior("synapses/bad.cs", bad, Guid.NewGuid()));

        Assert.Contains("CS0029", error.Message, StringComparison.Ordinal);
        Assert.Equal(["synapses/good.cs"], (await app.Read()).Files.Keys);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppWithNoProgramFilesRetiresInsteadOfDeploying()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "empty"));
        await app.Create(new("Empty", "", [], [new("Feed", "", "a")], []), "spec");

        await app.Configure(new() { ["Feed"] = "b" }, []);

        Assert.Empty(runtime.Deployed);
        Assert.Equal([IApp.Key("runtime-facts", "empty")], runtime.Retired);
    }
}
