using DigitalBrain.Testing;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> InoOperations
        => AppBaseline.Cases(
            typeof(InoApp),
            os =>
            {
                os.Register(typeof(AppsModule).Assembly);
                os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>();
            });

    [Theory, MemberData(nameof(InoOperations))]
    public Task InoBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task InoAutonomouslyRoutesSettingsNavigationAndGatesDestructiveSettingsActions()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("Where do I set up my local Ollama server?"));
        Assert.Equal("routed:settings.navigate:models:local-server", ino.App.State.Turns[^1].RouteHint);
        Assert.Contains(ino.App.State.ActiveCards, c => c.Kind == "settings" && c.SourceNeuronUri == "neuron://local/apps/settings");

        await ino.Request("send", new InoSend("Open Google Drive connection sheet in settings"));
        Assert.Equal("routed:settings.navigate:connections:google_drive:google_drive", ino.App.State.Turns[^1].RouteHint);

        await ino.Request("send", new InoSend("Revoke API key in settings"));
        Assert.Equal("routed:settings.navigate:keys", ino.App.State.Turns[^1].RouteHint);
        var approval = Assert.Single(ino.App.State.ActiveCards, c => c.Kind == "approval" && c.SourceNeuronUri == "neuron://local/os.settings/settings");
        Assert.Equal("pending", approval.Status);
    }

    [Fact]
    public async Task InoAndPostBddScenariosAndSystemNeuronsPass()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktopReport = await BddRunner.VerifyAsync<DesktopApp>(cancellationToken: ct);
        Assert.True(desktopReport.Passed);
        Assert.Single(desktopReport.Scenarios);

        var galleryReport = await BddRunner.VerifyAsync<GalleryApp>(
            fakes: os =>
            {
                os.Register(typeof(AppsModule).Assembly);
                os.Register(typeof(GalleryApp).Assembly);
            },
            cancellationToken: ct);
        Assert.True(galleryReport.Passed);
        Assert.Single(galleryReport.Scenarios);

        var photoCraftReport = await BddRunner.VerifyAsync<PhotoCraftApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(photoCraftReport.Passed);
        Assert.Single(photoCraftReport.Scenarios);

        var filmCraftReport = await BddRunner.VerifyAsync<FilmCraftApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(filmCraftReport.Passed);
        Assert.Single(filmCraftReport.Scenarios);

        var inoReport = await BddRunner.VerifyAsync<InoApp>(
            fakes: os =>
            {
                os.Register(typeof(AppsModule).Assembly);
                os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>();
            },
            cancellationToken: ct);
        Assert.True(inoReport.Passed);
        Assert.Single(inoReport.Scenarios);

        var postReport = await BddRunner.VerifyAsync<PostApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(postReport.Passed);
        Assert.Single(postReport.Scenarios);

        var creatorReport = await BddRunner.VerifyAsync<CreatorApp>(
            fakes: os =>
            {
                os.Register(typeof(CreatorApp).Assembly);
                os.Register<DigitalBrain.BddNeuron>();
                os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>();
            },
            cancellationToken: ct);
        Assert.True(creatorReport.Passed);
        Assert.Single(creatorReport.Scenarios);

        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(CreatorApp).Assembly);
        var settings = os.Get<IUserSettings>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "os.settings", "settings"));
        var updated = await settings.SetProfile("Vlad", "light", "gpt-5");
        Assert.Equal("Vlad", updated.DisplayName);
        Assert.Equal("light", updated.Theme);
        Assert.Equal("gpt-5", updated.DefaultModel);

        var profile = os.Get<IProfile>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "os.profile", "profile"));
        var pinned = await profile.SetBio("vlad", "AI OS Architect", ["creator"]);
        Assert.Equal("vlad", pinned.Handle);
        Assert.Equal("AI OS Architect", pinned.Bio);
        Assert.Contains("creator", pinned.PinnedApps);

        var inoInstalled = await os.Install<InoApp>();
        await settings.SetProfile("ElonWatcher", "dark", "qwen3.5:4b");
        await os.Idle();
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("Synced settings: ElonWatcher (qwen3.5:4b)", StringComparison.Ordinal));

        var uiIdentity = await settings.SetUiIdentity("spatial-glass", "compact-pill");
        Assert.Equal("spatial-glass", uiIdentity.StylePreset);
        Assert.Equal("compact-pill", uiIdentity.BarLayout);

        await inoInstalled.Request("send", new InoSend("Draft confirmation card"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("neuron://local/os.confirmations/inbox", StringComparison.Ordinal));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("scope:neuron://local/apps/ino#card-1", StringComparison.Ordinal));
        await inoInstalled.Request("approve-card", new InoApproveCard("card-1", true));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("approved", StringComparison.Ordinal));

        await inoInstalled.Request("send", new InoSend("Host=db.nordlys.internal;Database=nordlys_crm;Username=readonly"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("neuron://local/postgres/main", StringComparison.Ordinal));
        await inoInstalled.Request("send", new InoSend("SELECT account, city, owner FROM customers"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("neuron://local/postgres/main#query-result", StringComparison.Ordinal));

        await inoInstalled.Request("send", new InoSend("Remove background from portrait.png"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("layer.removeBackground", StringComparison.Ordinal));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("neuron://local/apps/photocraft", StringComparison.Ordinal));

        await inoInstalled.Request("send", new InoSend("Razor split timeline in FilmCraft via mcp"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("timeline.razor", StringComparison.Ordinal));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("neuron://local/apps/filmcraft", StringComparison.Ordinal));

        await inoInstalled.Request("send", new InoSend("/scenario invoice-check"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("Tripletex Session Token", StringComparison.Ordinal));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("Waiting on you", StringComparison.Ordinal));

        await inoInstalled.Request("send", new InoSend("https://intochat.app/a/tripletex-sync"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("Tripletex Accounting Sync (v1.4.0)", StringComparison.Ordinal));

        var photoCraftInstalled = await os.Install<PhotoCraftApp>();
        Assert.Equal("http://localhost:18082", photoCraftInstalled.App.State.WebUrl);
        await photoCraftInstalled.Request("remove-background", new PhotoCraftRemoveBackground("/work/hero.png", "native-linux"));
        Assert.Contains(photoCraftInstalled.Main.Texts, text => text.Contains("/work/hero.nobg.png", StringComparison.Ordinal));

        var filmCraftInstalled = await os.Install<FilmCraftApp>();
        Assert.Equal("http://localhost:18083", filmCraftInstalled.App.State.WebUrl);
        await filmCraftInstalled.Request("export", new FilmCraftExport("h264-1080p60", "/work/clip.fcproj", "native-linux"));
        Assert.Contains(filmCraftInstalled.Main.Texts, text => text.Contains("/work/clip.1080p60.mp4", StringComparison.Ordinal));

        var desktopInstalled = await os.Install<DesktopApp>();
        await desktopInstalled.Request("start");
        Assert.Contains("ino", desktopInstalled.App.State.PinnedApps);
        Assert.Contains("creator", desktopInstalled.App.State.PinnedApps);
        await desktopInstalled.Request("pin", new DesktopPinApp("photocraft", true));
        Assert.Contains("photocraft", desktopInstalled.App.State.PinnedApps);
        await desktopInstalled.Request("auto-arrange", new DesktopAutoArrange());
        Assert.Contains("Auto-Arranged", desktopInstalled.App.State.Status, StringComparison.Ordinal);

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

        await creatorInstalled.Request("language", new CreatorSwitchLanguage("es"));
        Assert.Equal("es", creatorInstalled.App.State.BddLanguage);

        await creatorInstalled.Request("hot-restart", new CreatorHotRestart());
        Assert.Equal(1, creatorInstalled.App.State.SandboxRestartCount);

        await creatorInstalled.Request("rollback", new CreatorRollback("1.0.0"));
        Assert.Equal("1.0.0", creatorInstalled.App.State.CurrentVersion);
    }

    [Fact]
    public async Task InoRoutesTaskCreateIntentAndHandsOffToTaskExecutor()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<IntoChat.OS.Modules.AI.Tasks.TaskNeuron>();
        os.Register<IntoChat.OS.Modules.AI.Tasks.TaskDirectoryNeuron>();
        os.Register<IntoChat.OS.Modules.AI.Tasks.TaskExecutorNeuron>();

        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("create task: Open files -> [Confirm] Merge PR"));

        Assert.StartsWith("routed:task.create:", ino.App.State.Turns[^1].RouteHint, StringComparison.Ordinal);
        Assert.Contains(ino.App.State.ActiveCards, c => c.Kind == "task" && c.SourceNeuronUri.StartsWith("neuron://local/tasks.task/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InoDrawsFunAndInlineChartsInChatAndNeverLeaksRecursiveCapabilityEpisodes()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>();

        var ino = await os.Install<InoApp>();

        // 1. Ask capability question first so it is recorded in MemoryNeuron
        await ino.Request("send", new InoSend("hey, what can you do?"));
        Assert.Contains("Installed IntoChat OS Brain Capabilities", ino.App.State.Turns[^1].Reply, StringComparison.Ordinal);

        // 2. Ask "Can you draw me a test cahrt with some fun data" (including the "cahrt" typo)
        await ino.Request("send", new InoSend("Can you draw me a test cahrt with some fun data"));
        var funChartCard = ino.App.State.ActiveCards.LastOrDefault(c => c.Kind == "chart");
        Assert.NotNull(funChartCard);
        Assert.Contains("Cosmic Coffee", funChartCard.Title, StringComparison.Ordinal);
        Assert.NotEmpty(funChartCard.ChartPoints ?? []);

        // 3. Ask for an inline custom pie chart
        await ino.Request("send", new InoSend("Draw a pie chart: Espresso=42, Matcha=29, ColdBrew=55"));
        var inlineChartCard = ino.App.State.ActiveCards.LastOrDefault(c => c.Kind == "chart");
        Assert.NotNull(inlineChartCard);
        Assert.Equal("pie", inlineChartCard.ChartKind);
        Assert.Equal(3, inlineChartCard.ChartPoints?.Length);

        // 4. Subsequent general chat turns must never contain recursive "(recalled ... (recalled" or capability dumps
        await ino.Request("send", new InoSend("hello there"));
        await ino.Request("send", new InoSend("how is the weather today"));
        var finalReply = ino.App.State.Turns[^1].Reply;
        Assert.DoesNotContain("Installed IntoChat OS Brain Capabilities", finalReply, StringComparison.Ordinal);
        var firstRecalledIdx = finalReply.IndexOf("(recalled ", StringComparison.Ordinal);
        if (firstRecalledIdx >= 0)
        {
            Assert.Equal(-1, finalReply.IndexOf("(recalled ", firstRecalledIdx + 10, StringComparison.Ordinal));
        }
    }
}
