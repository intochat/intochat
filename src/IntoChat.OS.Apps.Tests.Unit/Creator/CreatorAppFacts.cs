using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Creator;

public sealed class CreatorAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> CreatorOperations
        => AppBaseline.Cases(
            typeof(CreatorApp),
            os =>
            {
                os.Register<DigitalBrain.BddNeuron>();
                os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>();
            });

    [Theory, MemberData(nameof(CreatorOperations))]
    public Task CreatorBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task CreatorScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<CreatorApp>(
            fakes: os =>
            {
                os.Register<DigitalBrain.BddNeuron>();
                os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>();
            },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public async Task CreatorSessionNeuronPersistsPromptLanguageRollbackAndHotRestart()
    {
        await using var os = TestOS.Local();
        os.Register<CreatorSession>();
        var session = os.Get<ICreatorSession>(new NeuronId(new BrainId("local"), "os.creator.session", "session"));
        var prompted = await session.RecordPrompt("post", "Track Elon Musk posts", CreatorSession.DefaultSandboxCode, true, [], "uk");
        Assert.Equal("post", prompted.TargetApp);
        Assert.Equal("1.1.0", prompted.CurrentVersion);
        Assert.Equal("uk", prompted.BddLanguage);
        Assert.Contains("1.1.0", prompted.VersionHistory);

        var switched = await session.SwitchLanguage("es");
        Assert.Equal("es", switched.BddLanguage);

        var restarted = await session.HotRestart();
        Assert.Equal(1, restarted.SandboxRestartCount);

        var rolledBack = await session.Rollback("1.0.0", null);
        Assert.Equal("1.0.0", rolledBack.CurrentVersion);
    }

    [Fact]
    public async Task CreatorAppHandlesPromptLanguageHotRestartAndRollback()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>();
        var creatorInstalled = await os.Install<CreatorApp>();
        await creatorInstalled.Request("open");
        await creatorInstalled.Request("prompt", new CreatorPrompt("Track Elon Musk posts", "post", "uk"));
        Assert.True(creatorInstalled.App.State.SceneRotationPaused);
        Assert.Equal(1.65, creatorInstalled.App.State.CameraZoom);
        Assert.Equal("uk", creatorInstalled.App.State.BddLanguage);
        Assert.Equal("1.1.0", creatorInstalled.App.State.CurrentVersion);
        Assert.True(creatorInstalled.App.State.SandboxCompiled);
        Assert.Contains(creatorInstalled.Main.Texts, text => text.Contains("3D BrainScene", StringComparison.Ordinal));
        Assert.Contains(creatorInstalled.Main.Texts, text => text.Contains("SANDBOX / TEST CREDENTIALS", StringComparison.Ordinal));
        var creatorMapped = System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(creatorInstalled.App.Render(), "local/apps/creator/main"));
        Assert.Contains("pauseRotation", creatorMapped, StringComparison.Ordinal);
        Assert.Contains("1.65", creatorMapped, StringComparison.Ordinal);

        await creatorInstalled.Request("language", new CreatorSwitchLanguage("es"));
        Assert.Equal("es", creatorInstalled.App.State.BddLanguage);

        await creatorInstalled.Request("hot-restart", new CreatorHotRestart());
        Assert.Equal(1, creatorInstalled.App.State.SandboxRestartCount);

        await creatorInstalled.Request("rollback", new CreatorRollback("1.0.0"));
        Assert.Equal("1.0.0", creatorInstalled.App.State.CurrentVersion);
    }
}
