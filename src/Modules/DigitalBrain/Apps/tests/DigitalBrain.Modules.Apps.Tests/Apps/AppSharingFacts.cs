using DigitalBrain.Testing.Module;

namespace DigitalBrain.Apps.Tests.Apps;

// The share gate: only green versions leave a brain, and what leaves carries no credentials.
public sealed class AppSharingFacts
{
    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [new("gmail", "gmail")]);

    private static Task<ModuleBrain> Start() =>
        ModuleTest.Create().WithModule<AppsModule>().StartAsync(TestContext.Current.CancellationToken);

    private static async Task<IApp> LiveApp(ModuleBrain brain, string key)
    {
        var app = brain.Get<IApp>(key);
        await app.Create(Manifest(), "## Scenario: Echoes\nIt echoes.");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t", ["behaviors/echo.cs"] = "// b" }, null);
        return app;
    }

    [Fact]
    public async Task ExportingARedVersionIsRefused()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-a", "red"));
        await app.RecordVerdicts([new("Echoes", false, "broken")], green: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
        Assert.Contains("green", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportingBeforeAnyVerificationIsRefused()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-b", "unverified"));
        await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
    }

    [Fact]
    public async Task AVersionSavedAfterTheGreenRunCannotRideThatVerdict()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-c", "moved-on"));
        await app.RecordVerdicts([new("Echoes", true, "ok")], green: true);
        await app.WriteFile("notes.md", "changed after the green run", Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
    }

    [Fact]
    public async Task AnExportedBundleCarriesNoConnectionsAndDefaultSettings()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-d", "clean"));
        await app.Configure(new() { ["Text"] = "my private value" }, new() { ["gmail"] = "my-gmail-connection" });
        await app.RecordVerdicts([new("Echoes", true, "ok")], green: true);
        var bundle = await app.Export();
        var json = System.Text.Json.JsonSerializer.Serialize(bundle);
        Assert.DoesNotContain("my-gmail-connection", json, StringComparison.Ordinal);
        Assert.DoesNotContain("my private value", json, StringComparison.Ordinal);
        Assert.Equal("hi", bundle.Manifest.Settings.Single(setting => setting.Name == "Text").Default);
    }

    [Fact]
    public async Task ImportingIntoAnotherBrainYieldsALiveAppWithEmptyConnectionSlots()
    {
        await using var brain = await Start();
        var source = await LiveApp(brain, IApp.Key("sharing-e", "original"));
        await source.Configure(new(), new() { ["gmail"] = "alice-gmail" });
        await source.RecordVerdicts([new("Echoes", true, "ok")], green: true);
        var bundle = await source.Export();

        var copy = brain.Get<IApp>(IApp.Key("sharing-other-brain", "original"));
        var imported = await copy.Import(bundle);

        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Empty(imported.Connections);
        Assert.Equal((await source.Read()).Files, imported.Files);
    }

    [Fact]
    public async Task ImportingOverAnExistingAppIsRefusedAndLeavesItUntouched()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, IApp.Key("sharing-f", "mine"));
        var before = await app.Read();
        var bundle = new AppBundle("mine", "9.0.0", Manifest(), "## Scenario: Other\nOther.", new() { ["tests.cs"] = "// other" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Import(bundle));
        var after = await app.Read();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Spec, after.Spec);
    }

    [Fact]
    public async Task ImportRefusesAnInvalidNameOrAnUnparsableVersion()
    {
        await using var brain = await Start();
        var sneaky = brain.Get<IApp>(IApp.Key("sharing-h", "x/apps/y"));
        await Assert.ThrowsAsync<InvalidDataException>(() => sneaky.Import(
            new("x/apps/y", "1.0.0", Manifest(), "spec", new() { ["tests.cs"] = "// t" })));
        var odd = brain.Get<IApp>(IApp.Key("sharing-h", "odd"));
        await Assert.ThrowsAsync<InvalidDataException>(() => odd.Import(
            new("odd", "1.0.0-rc1", Manifest(), "spec", new() { ["tests.cs"] = "// t" })));
    }

    [Fact]
    public async Task EditingAnImportedAppBumpsItsOwnHistoryFromTheImportedBaseline()
    {
        await using var brain = await Start();
        var copy = brain.Get<IApp>(IApp.Key("sharing-g", "forked"));
        await copy.Import(new("forked", "1.2.0", Manifest(), "## Scenario: Echoes\nIt echoes.", new() { ["tests.cs"] = "// t" }));
        var edited = await copy.WriteFile("notes.md", "my own notes", Guid.NewGuid());
        Assert.Equal("1.2.1", edited.Version);
        Assert.Contains(edited.Versions, version => version.Version == "1.2.0");
    }
}
