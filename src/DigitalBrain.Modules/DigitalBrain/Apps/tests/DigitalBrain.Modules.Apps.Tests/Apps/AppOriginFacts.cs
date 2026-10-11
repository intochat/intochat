using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing;

namespace DigitalBrain.Apps.Tests.Apps;

// An installed copy remembers where it came from; the first edit makes it the person's own.
public sealed class AppOriginFacts
{
    private static readonly AppOrigin Origin = new("intochat", new AppSource.Brain("account-intochat", "intochat"), "echo", "1.0.0");

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [], Icon: "icon.svg");

    private static Task<ITestBrain> Start() =>
        TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(TestContext.Current.CancellationToken);

    private static async Task<IApp> Installed(ITestBrain brain, string key)
    {
        var app = brain.Get<IApp>(key);
        await app.Import(new("echo", "1.0.0", Manifest(), "## Scenario: Echoes\nIt echoes.",
            new() { ["icon.svg"] = "<svg/>" }));
        await app.RecordOrigin(Origin);
        return app;
    }

    [Fact]
    public async Task RecordingAnOriginShowsItOnTheSnapshotAndTheCopyStartsUnedited()
    {
        await using var brain = await Start();
        var snapshot = await (await Installed(brain, IApp.Key("origin-a", "echo"))).Read();
        Assert.Equal(Origin, snapshot.Origin);
        Assert.False(snapshot.Edited);
        Assert.Equal("icon.svg", snapshot.Manifest.Icon);
    }

    [Fact]
    public async Task EveryProgramEditMarksTheCopyEdited()
    {
        await using var brain = await Start();
        var spec = await Installed(brain, IApp.Key("origin-b", "echo"));
        Assert.True((await spec.SaveSpec("## Scenario: Echoes\nIt echoes twice.", Guid.NewGuid())).Edited);

        var manifest = await Installed(brain, IApp.Key("origin-c", "echo"));
        Assert.True((await manifest.SaveManifest(Manifest() with { Title = "Mine" }, Guid.NewGuid())).Edited);

        var file = await Installed(brain, IApp.Key("origin-d", "echo"));
        Assert.True((await file.WriteFile("notes.md", "mine", Guid.NewGuid())).Edited);

        var behavior = await Installed(brain, IApp.Key("origin-e", "echo"));
        Assert.True((await behavior.WriteBehavior("synapses/echo.cs", "// mine", Guid.NewGuid())).Edited);

        var rolled = await Installed(brain, IApp.Key("origin-f", "echo"));
        Assert.True((await rolled.Rollback("1.0.0")).Edited);

        var built = await Installed(brain, IApp.Key("origin-g", "echo"));
        await built.BeginBuild(null);
        Assert.True((await built.CompleteBuild(await built.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// built" }, null)).Edited);
    }

    [Fact]
    public async Task ConfiguringSettingsIsNotAnEdit()
    {
        await using var brain = await Start();
        var app = await Installed(brain, IApp.Key("origin-h", "echo"));
        var configured = await app.Configure(new() { ["Text"] = "mine" }, new());
        Assert.False(configured.Edited);
        Assert.Equal(Origin, configured.Origin);
    }

    [Fact]
    public async Task AFailedBuildIsNotAnEdit()
    {
        await using var brain = await Start();
        var app = await Installed(brain, IApp.Key("origin-i", "echo"));
        await app.BeginBuild(null);
        Assert.False((await app.CompleteBuild(await app.CurrentBuildAttempt(), null, "the model gave up")).Edited);
    }

    [Fact]
    public async Task RecordingAnOriginAgainClearsEdited()
    {
        await using var brain = await Start();
        var app = await Installed(brain, IApp.Key("origin-j", "echo"));
        await app.WriteFile("notes.md", "mine", Guid.NewGuid());
        await app.RecordOrigin(Origin with { Version = "1.1.0" });
        var snapshot = await app.Read();
        Assert.False(snapshot.Edited);
        Assert.Equal("1.1.0", snapshot.Origin!.Version);
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.10", "1.2.9", 1)]
    public void VersionsCompareNumerically(string left, string right, int sign)
        => Assert.Equal(sign, Math.Sign(AppVersions.Compare(left, right)));

    [Fact]
    public void AVersionThatIsNotMajorMinorPatchIsRefused()
        => Assert.Throws<FormatException>(() => AppVersions.Compare("1.0", "1.0.0"));
}
