using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
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
                os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
            });

    [Theory, MemberData(nameof(InoOperations))]
    public Task InoBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

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

    private static readonly BrainId Brain = new("local");

    private static TestOS AgentOs()
    {
        var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        return os;
    }

    private static NeuronId AgentOf(string conversationId) => new(Brain, AgentSignals.NeuronType, $"ino/conversations/{conversationId}");

    [Fact]
    public async Task OneSendStartsExactlyOneAgentRun()
    {
        await using var os = AgentOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("hello there"));
        await os.Idle();

        Assert.Equal("ino/conversations/main", ino.App.State.AgentKey);
        var started = Assert.Single(os.Journal(Brain).Signals(AgentOf("main")).Select(entry => entry.Signal).OfType<TurnStarted>());
        Assert.Equal("hello there", started.UserText);
        Assert.Null(ino.App.State.ActiveRunId);
    }

    [Fact]
    public void AToolResultWithACardRendersACard()
    {
        var card = new InoCard("settings-models", "settings", "Settings · models / local-server", "Open settings", "neuron://local/apps/settings", "ready");
        var run = new AgentRun("r1", AgentRunStatus.Completed, "where is my ollama server?", DateTimeOffset.UtcNow)
        {
            ToolCalls =
            [
                new AgentToolCall("c1", "neuron_call", "{}", System.Text.Json.JsonSerializer.Serialize(new InoToolResult("Opened settings.", card), System.Text.Json.JsonSerializerOptions.Web)),
                new AgentToolCall("c2", "neuron_call", "{}", """{"text":"no card"}"""),
                new AgentToolCall("c3", "neuron_call", "{}", Error: "denied"),
            ],
        };

        var cards = InoCards.FromToolCalls(run.ToolCalls);

        Assert.Equal(card, Assert.Single(cards));
        var view = InoViewRenderer.Render(new InoAppState() with { Cards = cards }, []);
        Assert.Contains("Settings · models / local-server", Strings(view));
    }

    // Every string a rendered view holds, walked through its node records.
    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [],
        string text => [text],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Strings),
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Strings(p.GetValue(value))),
        _ => [],
    };

    [Fact]
    public async Task NewRotatesTheAgentKey()
    {
        await using var os = AgentOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("first"));
        var first = ino.App.State.AgentKey;

        await ino.Request("new", new InoNew());
        await ino.Request("send", new InoSend("second"));

        Assert.NotNull(first);
        Assert.NotEqual(first, ino.App.State.AgentKey);
        Assert.Equal($"ino/conversations/{ino.App.State.ConversationId}", ino.App.State.AgentKey);
    }

    [Fact]
    public async Task ABlockedAgentShowsPaused()
    {
        await using var os = AgentOs();
        var ino = await os.Install<InoApp>();
        var focus = os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/ui.focus"));
        await Task.Run(async () =>
        {
            using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Platform("p1", "a1", Brain.Value));
            await focus.Set(new("stop", "maintenance window", [], [ContextTags.Interrupt]));
        }, TestContext.Current.CancellationToken);

        await ino.Request("send", new InoSend("hello"));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal("Paused", ino.App.State.Status);
        var bar = Assert.Single(ino.Main.Nodes.OfType<InfoBar>());
        Assert.Equal("critical", bar.Tone);
        Assert.Contains("maintenance window", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSidePanelListsAgentGrants()
    {
        await using var os = AgentOs();
        os.Register<GrantsNeuron>();
        var grants = os.Get<IGrantsNeuron>(new NeuronId(Brain, IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
#pragma warning disable CS0618 // the AppId agent tool calls run under
        await grants.Grant(new Grant { AppId = CapabilityCallers.AssistantApp, Scope = "run", Target = "local/ino/settings", WorkspaceId = "local" });
#pragma warning restore CS0618
        await grants.Grant(new Grant { AppId = "other", Scope = "run", Target = "local/elsewhere", WorkspaceId = "local" });
        var ino = await os.Install<InoApp>();

        await ino.Request("open", new InoOpen());

        Assert.Contains(ino.Side.Entries, entry => entry.Title == "run · local/ino/settings");
        Assert.DoesNotContain(ino.Side.Entries, entry => entry.Title.Contains("elsewhere", StringComparison.Ordinal));
    }
}
