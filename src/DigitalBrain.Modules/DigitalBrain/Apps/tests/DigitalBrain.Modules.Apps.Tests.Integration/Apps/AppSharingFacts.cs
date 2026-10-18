using DigitalBrain.Modules.Apps.Tests.Integration.Runtimes;
using DigitalBrain.Testing;

namespace DigitalBrain.Apps.Tests.Apps;

// The share gate: what leaves a brain compiles and carries no credentials.
public sealed class AppSharingFacts
{
    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [new("gmail", "gmail")]);

    private static Task<ITestBrain> Start() =>
        TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(TestContext.Current.CancellationToken);

    private static async Task<IApp> LiveApp(ITestBrain brain, string key)
    {
        var app = brain.Get<IApp>(key);
        await app.Create(Manifest(), "## Scenario: Echoes\nIt echoes.");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// b" }, null);
        return app;
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnExportedBundleCarriesNoConnectionsAndDefaultSettings()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-d", "clean"));
        await app.Configure(new() { ["Text"] = "my private value" }, new() { ["gmail"] = "my-gmail-connection" });
        var bundle = await app.Export();
        var json = System.Text.Json.JsonSerializer.Serialize(bundle);
        Assert.DoesNotContain("my-gmail-connection", json, StringComparison.Ordinal);
        Assert.DoesNotContain("my private value", json, StringComparison.Ordinal);
        Assert.Equal("hi", bundle.Manifest.Settings.Single(setting => setting.Name == "Text").Default);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ImportingIntoAnotherBrainYieldsALiveAppWithEmptyConnectionSlots()
    {
        await using var brain = await Start();
        var source = await LiveApp(brain, IApp.Key("sharing-e", "original"));
        await source.Configure(new(), new() { ["gmail"] = "alice-gmail" });
        var bundle = await source.Export();

        var copy = brain.Get<IApp>(IApp.Key("sharing-other-brain", "original"));
        var imported = await copy.Import(bundle);

        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Empty(imported.Connections);
        Assert.Equal((await source.Read()).Files, imported.Files);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ImportingOverAnExistingAppIsRefusedAndLeavesItUntouched()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-f", "mine"));
        var before = await app.Read();
        var bundle = new AppBundle("mine", "9.0.0", Manifest(), "## Scenario: Other\nOther.", new() { ["synapses/echo.cs"] = "// other" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Import(bundle));
        var after = await app.Read();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Spec, after.Spec);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ImportRefusesAnInvalidNameOrAnUnparsableVersion()
    {
        await using var brain = await Start();
        var sneaky = brain.Get<IApp>(IApp.Key("sharing-h", "x/apps/y"));
        await Assert.ThrowsAsync<InvalidDataException>(() => sneaky.Import(
            new("x/apps/y", "1.0.0", Manifest(), "spec", new() { ["synapses/echo.cs"] = "// t" })));
        var odd = brain.Get<IApp>(IApp.Key("sharing-h", "odd"));
        await Assert.ThrowsAsync<InvalidDataException>(() => odd.Import(
            new("odd", "1.0.0-rc1", Manifest(), "spec", new() { ["synapses/echo.cs"] = "// t" })));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task EditingAnImportedAppBumpsItsOwnHistoryFromTheImportedBaseline()
    {
        await using var brain = await Start();
        var copy = brain.Get<IApp>(IApp.Key("sharing-g", "forked"));
        await copy.Import(new("forked", "1.2.0", Manifest(), "## Scenario: Echoes\nIt echoes.", new() { ["synapses/echo.cs"] = "// t" }));
        var edited = await copy.WriteFile("notes.md", "my own notes", Guid.NewGuid());
        Assert.Equal("1.2.1", edited.Version);
        Assert.Contains(edited.Versions, version => version.Version == "1.2.0");
    }
}
