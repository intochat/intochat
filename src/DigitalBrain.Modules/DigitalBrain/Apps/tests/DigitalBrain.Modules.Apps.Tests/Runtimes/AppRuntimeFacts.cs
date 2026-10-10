using DigitalBrain.Apps;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

// The runtime is the one seam an app deploys through: the whole program, with its configuration,
// every time. A host without a csharp module registers no runtime and apps are data only.
internal sealed class FakeAppRuntime : IAppRuntime
{
    public List<(string AppKey, Dictionary<string, string> Sources, Dictionary<string, string> Configuration)> Deployed { get; } = [];
    public List<string> Retired { get; } = [];
    public Func<Dictionary<string, string>, Exception?>? Refuse { get; set; }

    public Task Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
    {
        if (Refuse?.Invoke(sources) is { } error) { throw error; }
        Deployed.Add((appKey, new(sources, StringComparer.Ordinal), new(configuration, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    public Task Retire(string appKey, CancellationToken cancellationToken) { Retired.Add(appKey); return Task.CompletedTask; }

    public static Task<ModuleBrain> Start(FakeAppRuntime runtime, CancellationToken ct)
        => ModuleTest.Create().WithModule<AppsModule>().WithFakeAppRuntime(runtime).StartAsync(ct);
}

internal static class AppRuntimeHosting
{
    // A host that composes the Apps without a csharp module has no brain runtime to deploy into, so a
    // recording runtime stands in for the real one; the app's deploy path is exercised either way.
    public static TestBrain WithFakeAppRuntime(this TestBrain builder, FakeAppRuntime runtime)
        => builder.ConfigureSilo(silo =>
        {
            silo.Services.RemoveAll<IAppRuntime>();
            silo.Services.AddSingleton<IAppRuntime>(runtime);
        });
}

public sealed class AppRuntimeFacts
{
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

    [Fact]
    public async Task AWriteWhoseProgramDoesNotCompileFailsAndKeepsThePreviousFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime { Refuse = sources => sources.ContainsKey("synapses/bad.cs") ? new InvalidOperationException("The program does not compile: synapses/bad.cs line 1 CS0029") : null };
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = brain.Get<IApp>(IApp.Key("runtime-facts", "strict"));
        await app.Create(new("Strict", "", [], [], []), "spec");
        await app.WriteBehavior("synapses/good.cs", "// ok", Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.WriteBehavior("synapses/bad.cs", "int x = \"no\";", Guid.NewGuid()));

        Assert.Contains("CS0029", error.Message, StringComparison.Ordinal);
        Assert.Equal(["synapses/good.cs"], (await app.Read()).Files.Keys);
    }

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
