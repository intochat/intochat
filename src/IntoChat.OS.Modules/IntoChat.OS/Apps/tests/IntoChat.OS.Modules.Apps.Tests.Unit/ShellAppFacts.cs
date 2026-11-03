using DigitalBrain.Testing;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ShellAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> DesktopOperations => AppBaseline.Cases(typeof(DesktopApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> InoOperations
        => AppBaseline.Cases(typeof(InoApp), os => os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>());

    [Theory, MemberData(nameof(DesktopOperations))]
    public Task DesktopBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(InoOperations))]
    public Task InoBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public void WorkspaceAndDesktopCarryTheFlutterTrees()
    {
        Assert.Equal("workspace", WorkspaceApp.Bundle.Name);
        Assert.Equal("desktop", DesktopApp.Bundle.Name);
        Assert.Contains("Apps", DesktopApp.Bundle.Files["DesktopLayout.cs"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings", "cloud", true)]
    [InlineData("settings", " local ", false)]
    [InlineData("settings", "cloud ", true)]
    [InlineData("workspace", "cloud", false)]
    [InlineData("settings", null, false)]
    public void TheSettingsAppWritesTheAccountSurfaceOnlyOnACloudHost(string app, string? topology, bool writes)
        => Assert.Equal(writes, AppNeuron.WritesAccountSurface(app, topology));

    [Fact]
    public void ModuleListNamesTheAppsModuleAndNoSecretFields()
    {
        _ = typeof(AppsModule);
        var rows = InstalledModules.List();
        var apps = Assert.Single(rows, row => row.Id == "apps");
        Assert.Equal(nameof(AppsModule), apps.Name);
        var json = InstalledModules.Json();
        Assert.Contains("\"id\":\"apps\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"account\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAppsRootIsTheSideloadDirectory()
    {
        var root = Directory.CreateTempSubdirectory("apps-root-").FullName;
        try
        {
            var disk = new Disk();
            var full = AppsDirectoryRoot.Prepare(root);
            await AppsDirectoryRoot.Allow(disk, root);
            Assert.Equal(AppsDirectoryRoot.Label, "Apps");
            Assert.Equal(full, disk.Allowed);
            Assert.True(Directory.Exists(full));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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

        var galleryReport = await BddRunner.VerifyAsync<GalleryApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(galleryReport.Passed);
        Assert.Single(galleryReport.Scenarios);

        var photoCraftReport = await BddRunner.VerifyAsync<PhotoCraftApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(photoCraftReport.Passed);
        Assert.Single(photoCraftReport.Scenarios);

        var filmCraftReport = await BddRunner.VerifyAsync<FilmCraftApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(filmCraftReport.Passed);
        Assert.Single(filmCraftReport.Scenarios);

        var inoReport = await BddRunner.VerifyAsync<InoApp>(fakes: os => os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>(), cancellationToken: ct);
        Assert.True(inoReport.Passed);
        Assert.Single(inoReport.Scenarios);

        var postReport = await BddRunner.VerifyAsync<PostApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(postReport.Passed);
        Assert.Single(postReport.Scenarios);

        var creatorReport = await BddRunner.VerifyAsync<CreatorApp>(
            fakes: os =>
            {
                os.Register<DigitalBrain.BddNeuron>();
                os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>();
            },
            cancellationToken: ct);
        Assert.True(creatorReport.Passed);
        Assert.Single(creatorReport.Scenarios);

        await using var os = TestOS.Local();
        os.Register<UserSettingsNeuron>();
        os.Register<ProfileNeuron>();
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
        var photoCraftMapped = System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(photoCraftInstalled.App.Render(), "local/apps/photocraft/main"));
        Assert.Contains("webbrowser", photoCraftMapped, StringComparison.Ordinal);
        Assert.Contains("http://localhost:18082", photoCraftMapped, StringComparison.Ordinal);

        var filmCraftInstalled = await os.Install<FilmCraftApp>();
        Assert.Equal("http://localhost:18083", filmCraftInstalled.App.State.WebUrl);
        await filmCraftInstalled.Request("export", new FilmCraftExport("h264-1080p60", "/work/clip.fcproj", "native-linux"));
        Assert.Contains(filmCraftInstalled.Main.Texts, text => text.Contains("/work/clip.1080p60.mp4", StringComparison.Ordinal));
        var filmCraftMapped = System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(filmCraftInstalled.App.Render(), "local/apps/filmcraft/main"));
        Assert.Contains("webbrowser", filmCraftMapped, StringComparison.Ordinal);
        Assert.Contains("http://localhost:18083", filmCraftMapped, StringComparison.Ordinal);

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

    private sealed class Disk : ILocalDisk
    {
        public string? Allowed { get; private set; }
        public Task<string[]> Roots() => Task.FromResult(Allowed is null ? [] : new[] { Allowed });
        public Task AllowRoot(string path) { Allowed = path; return Task.CompletedTask; }
        public Task RevokeRoot(string path) => Task.CompletedTask;
    }
}
