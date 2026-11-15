using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoAppFacts
{
    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class OfflineLocalProbe : ILocalOllamaProbe
    {
        public Task<LocalOllamaDetection?> Detect(CancellationToken ct = default)
            => Task.FromResult<LocalOllamaDetection?>(null);
    }

    private static IServiceProvider WithRedactor(IServiceProvider? parent = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRedactor, NoSecrets>();
        services.AddSingleton<ILocalOllamaProbe, OfflineLocalProbe>();
        services.AddSingleton(TimeProvider.System);
        var provider = services.BuildServiceProvider();
        if (parent is null) { return provider; }
        return new FallbackServiceProvider(provider, parent);
    }

    private sealed class FallbackServiceProvider(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType) => primary.GetService(serviceType) ?? fallback.GetService(serviceType);
    }

    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> InoOperations
        => ScreenBaseline.Cases(
            typeof(InoApp),
            os =>
            {
                os.Register(typeof(AppsModule).Assembly);
            });

    [Theory, MemberData(nameof(InoOperations))]
    public Task InoBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public void InoAgentDefinitionExposesStudioMemoryAspireTraceReaderAndFlows()
    {
        var def = InoAgentDefinition.For(new UserSettingsState() with { DisplayName = "Vlad", DefaultModel = "qwen2.5-coder:14b" });
        Assert.Contains(typeof(IInoStudio).FullName!, def.Contracts);
        Assert.Contains(typeof(IMemory).FullName!, def.Contracts);
        Assert.Contains(typeof(IAspire).FullName!, def.Contracts);
        Assert.Contains(typeof(ITraceReader).FullName!, def.Contracts);
        Assert.Contains(typeof(IFlows).FullName!, def.Contracts);
        Assert.Contains("IntoChat.OS.Modules.Microsoft.Roslyn.ICodeGraph", def.Contracts);
        Assert.Contains("ProposeChange", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("RememberPreference", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("GetTracesAsync", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Modules.Microsoft.Roslyn.ICodeGraph", def.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("IntoChat.OS.Modules.Roslyn.ICodeGraph", def.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoAndPostBddScenariosAndSystemNeuronsPass()
    {
        using var person = Edge.AsPerson();
        await using var checkOs = TestOS.Local(options => options.Services = WithRedactor());
        checkOs.Register<DesktopRecordsNeuron>();
        checkOs.Register(typeof(AppsModule).Assembly);
        checkOs.Register(typeof(InoApp).Assembly);
        checkOs.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);

        var desktopScreen = checkOs.Get<IScreen>(new NeuronId(new BrainId("local"), "app.desktop", "default"));
        Assert.NotNull(await desktopScreen.Read());
        var galleryScreen = checkOs.Get<IScreen>(new NeuronId(new BrainId("local"), "app.gallery", "default"));
        Assert.NotNull(await galleryScreen.Read());
        var photoCraftScreen = checkOs.Get<IScreen>(new NeuronId(new BrainId("local"), "app.photocraft", "default"));
        Assert.NotNull(await photoCraftScreen.Read());
        var filmCraftScreen = checkOs.Get<IScreen>(new NeuronId(new BrainId("local"), "app.filmcraft", "default"));
        Assert.NotNull(await filmCraftScreen.Read());
        var inoScreen = checkOs.Get<IScreen>(new NeuronId(new BrainId("local"), "app.ino", "default"));
        Assert.NotNull(await inoScreen.Read());
        var friendsScreen = checkOs.Get<IScreen>(new NeuronId(new BrainId("local"), "app.friends", "default"));
        Assert.NotNull(await friendsScreen.Read());

        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(CreatorApp).Assembly);
        var settings = os.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var updated = await settings.SetProfile("Vlad", "light", "gpt-5");
        Assert.Equal("Vlad", updated.DisplayName);
        Assert.Equal("light", updated.Theme);
        Assert.Equal("gpt-5", updated.DefaultModel);

        var profile = os.Get<IProfile>(new NeuronId(new BrainId("local"), "os.profile", "profile"));
        var pinned = await profile.SetBio("vlad", "AI OS Architect", ["creator"]);
        Assert.Equal("vlad", pinned.Handle);
        Assert.Equal("AI OS Architect", pinned.Bio);
        Assert.Contains("creator", pinned.PinnedApps);

        var inoInstalled = await os.Install<InoApp>();
        await settings.SetProfile("ElonWatcher", "dark", "qwen3.5:4b");
        await os.Idle();
        Assert.Contains("Synced settings: ElonWatcher (qwen3.5:4b)", inoInstalled.App.State.Status, StringComparison.Ordinal);

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
    }

    private static readonly BrainId Brain = new("local");

    private static TestOS AgentOs()
    {
        var os = TestOS.Local(options => options.Services = WithRedactor());
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
        if (ino.App.State.ActiveRunId is not null)
        {
            Assert.Equal(started.RunId, ino.App.State.ActiveRunId);
        }
        else
        {
            Assert.Equal(InoModel.Failed, ino.App.State.Status);
            Assert.Equal("no-provider", ino.App.State.Error?.Code);
        }
    }

    [Fact]
    public async Task RuntimeModelSwitchViaSlashCommandSettingsAppAndUserSettingsPropagatesToAgentAndProfileContext()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        var settingsApp = await os.Install<SettingsApp>();
        var userSettings = os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings"));

        await ino.Request("send", new InoSend("turn 1"));
        Assert.Equal("qwen3.5:4b", BackgroundAgent.LastConfiguredModel);
        Assert.Equal("qwen3.5:4b", BackgroundAgent.LastTurnModel);
        BackgroundChat.Complete("local/ino/conversations/main", "run-1", "turn 1", "reply 1");
        await Deliver(ino, new ChatRunChanged("local/ino/chats/main", BackgroundChat.Revision, ChatRunStatus.Completed));
        BackgroundAgent.Starts = 0;

        await ino.Request("send", new InoSend("/model claude-sonnet-4-5"));
        Assert.Equal("Switched model to claude-sonnet-4-5", ino.App.State.Status);
        await os.Idle();
        Assert.Contains("claude-sonnet-4-5", ino.App.State.Status, StringComparison.Ordinal);
        Assert.Equal("claude-sonnet-4-5", (await userSettings.Read()).DefaultModel);
        Assert.Equal("claude-sonnet-4-5", BackgroundAgent.LastConfiguredModel);

        await ino.Request("send", new InoSend("turn 2"));
        Assert.Equal("claude-sonnet-4-5", BackgroundAgent.LastTurnModel);
        BackgroundChat.Complete("local/ino/conversations/main", "run-1", "turn 2", "reply 2");
        await Deliver(ino, new ChatRunChanged("local/ino/chats/main", BackgroundChat.Revision, ChatRunStatus.Completed));
        BackgroundAgent.Starts = 0;

        await settingsApp.Request("save-preferences", new SettingsSavePreferences(
            DisplayName: "Ada Lovelace",
            Theme: "dark",
            DefaultModel: "gpt-5",
            StylePreset: "fluent-acrylic",
            BarLayout: "floating-glass"));
        await os.Idle();
        Assert.Equal("gpt-5", (await userSettings.Read()).DefaultModel);
        Assert.Equal("gpt-5", BackgroundAgent.LastConfiguredModel);

        await ino.Request("send", new InoSend("turn 3"));
        Assert.Equal("gpt-5", BackgroundAgent.LastTurnModel);

        await using var agentOs = AgentOs();
        var liveIno = await agentOs.Install<InoApp>();
        var liveProfile = agentOs.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/user.profile"));
        var liveAgent = agentOs.Get<IAgent>(AgentOf("main"));
        await liveIno.Request("send", new InoSend("/model claude-sonnet-4-5"));
        await agentOs.Idle();
        Assert.Equal("claude-sonnet-4-5", (await liveAgent.Read()).Definition.Model?.Model);
        Assert.Equal("""{"model":"claude-sonnet-4-5"}""", Assert.Single((await liveProfile.Read()).Entries, e => e.Name == "inference").Text);
    }

    [Fact]
    public async Task NewConversationMovesTheWatchBeforeTheNextSend()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("first"));

        await ino.Request("new", new InoNew());

        var current = await os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey)).Current();
        Assert.Equal([$"ino/conversations/{ino.App.State.ConversationId}"], current);
        Assert.Equal(1, BackgroundAgent.Starts);
    }

    [Fact]
    public async Task SendStartsExactlyOnceAndReturnsBeforeCompletion()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("hello"));

        Assert.Equal(1, BackgroundAgent.Starts);
        Assert.Equal(0, BackgroundAgent.Runs);
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        Assert.Equal(InoModel.Working, ino.App.State.Status);
        Assert.Equal(["ino/conversations/main"], await os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey)).Current());
    }

    [Fact]
    public async Task TurnCompletedUpdatesConversationAndRecordsCompute()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        BackgroundAgent.Reply = "Hi from the background";
        BackgroundChat.Complete("local/ino/conversations/main", "run-1", "hello", "Hi from the background");

        await Deliver(ino, new ChatRunChanged("local/ino/chats/main", BackgroundChat.Revision, ChatRunStatus.Completed));

        Assert.Equal(InoModel.Ready, ino.App.State.Status);
        Assert.Null(ino.App.State.ActiveRunId);
        Assert.Contains("Hi from the background", Texts(ino.App.Render()));
        Assert.Equal(("ino", 1_000L), FakeCompute.Records.Single());
    }

    [Fact]
    public async Task AgentBlockedPauses()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));

        await Deliver(ino, new AgentBlocked("ino/conversations/main", true));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal(InoModel.Paused, ino.App.State.Status);
        Assert.Contains("Ino is paused.", Texts(ino.App.Render()));
    }

    [Fact]
    public async Task StopCancelsActiveRun()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Contains("Stop", Texts(ino.App.Render()));

        await ino.Request("stop", new InoStop());

        Assert.Equal(1, BackgroundAgent.Cancels);
    }

    [Fact]
    public async Task SmartToggleWritesVariable()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("smart", new InoSmart(true));

        var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
        Assert.Contains(settings.Variables, v => v.Key == "ino.smart" && v.Value == "on");
        Assert.True(ino.App.State.Smart);
    }

    private static TestOS BackgroundOs()
    {
        BackgroundAgent.Reset();
        BackgroundChat.Reset();
        FakeCompute.Reset();
        var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<BackgroundAgent>();
        os.Register<BackgroundChat>();
        os.Register<FakeCompute>();
        return os;
    }

    private static Task Deliver<TSignal>(InstalledApp<InoApp> ino, TSignal signal) where TSignal : Signal
        => signal switch
        {
            ChatRunChanged c => ino.App.OnChatRunChanged(c),
            AgentBlocked b => ino.App.OnAgentBlocked(b),
            _ => throw new NotSupportedException($"Unknown signal {typeof(TSignal).Name}"),
        };

    [Fact]
    public void AToolResultWithACardRendersACard()
    {
        var card = new InoCard("settings-models", "settings", "Settings · models / local-server", "Open settings", "neuron://local/apps/settings", "ready");
        var serialized = System.Text.Json.JsonSerializer.Serialize(new InoToolResult("Opened settings.", card), System.Text.Json.JsonSerializerOptions.Web);
        var chat = new ChatState
        {
            Name = "local/ino/chats/main",
            Revision = 1,
            Selection = new ChatSelection("local/ino/conversations/main", 1),
            Projection = new ChatProjection(
                "local/ino/conversations/main",
                1,
                1,
                [
                    new ChatEntry("r1/user", ChatRole.User, "where is my ollama server?"),
                    new ChatEntry("r1/assistant", ChatRole.Assistant, "Opened settings.")
                    {
                        Activities =
                        [
                            new ChatActivity("c1", "neuron_call", "{}", serialized),
                            new ChatActivity("c2", "neuron_call", "{}", """{"text":"no card"}"""),
                            new ChatActivity("c3", "neuron_call", "{}", Error: "denied"),
                        ],
                    },
                ]),
            Run = new ChatRunSnapshot("local/ino/conversations/main", 1, 1, "r1", ChatRunStatus.Completed),
        };

        var state = InoModel.Projected(new InoAppState(), chat, "local/ino/conversations/main");
        Assert.Equal(card, Assert.Single(state.ActiveCards));
        var view = InoView.Screen(state);
        Assert.Contains("Settings · models / local-server", Texts(view));
    }

    private static IReadOnlyList<string> Texts(View view)
        => new PresentedView("main", view).Texts;

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
    public async Task NewConversationMintsCanonicalConversationIdAndKey()
    {
        await using var os = AgentOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("new", new InoNew());

        var convId = ino.App.State.ConversationId;
        Assert.NotNull(convId);
        Assert.StartsWith("c-", convId, StringComparison.Ordinal);
        Assert.Equal(34, convId.Length);
        Assert.True(ConversationId.IsCanonicalValue(convId));
        Assert.Equal($"ino/conversations/{convId}", ino.App.State.AgentKey);

        await ino.Request("new-conversation", new InoNewConversation());
        var secondConvId = ino.App.State.ConversationId;
        Assert.NotNull(secondConvId);
        Assert.StartsWith("c-", secondConvId, StringComparison.Ordinal);
        Assert.Equal(34, secondConvId.Length);
        Assert.True(ConversationId.IsCanonicalValue(secondConvId));
        Assert.NotEqual(convId, secondConvId);
    }

    [Fact]
    public async Task InoRenderZeroNeuronUriAndScopeLeaksInRenderedView()
    {
        await using var os = AgentOs();
        var ino = await os.Install<InoApp>();

        var initialTexts = Texts(ino.App.Render());
        Assert.All(initialTexts, text =>
        {
            Assert.DoesNotContain("neuron://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("| scope:", text, StringComparison.OrdinalIgnoreCase);
        });

        await ino.Request("send", new InoSend("/scenario morning-brief"));
        var scenarioTexts = Texts(ino.App.Render());
        Assert.All(scenarioTexts, text =>
        {
            Assert.DoesNotContain("neuron://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("| scope:", text, StringComparison.OrdinalIgnoreCase);
        });

        await ino.Request("make", new InoMake("Build calculator", ["calculator"]));
        var makeTexts = Texts(ino.App.Render());
        Assert.All(makeTexts, text =>
        {
            Assert.DoesNotContain("neuron://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("| scope:", text, StringComparison.OrdinalIgnoreCase);
        });
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
        Assert.Equal(InoModel.Paused, ino.App.State.Status);
        var c = InoView.Conversation(ino.App.State);
        Assert.NotNull(c.Error);
        Assert.Equal("Ino is paused.", c.Error!.What);
        Assert.Contains("maintenance window", c.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSidePanelListsAgentGrants()
    {
        await using var os = AgentOs();
        os.Register<GrantsNeuron>();
        var grants = os.Get<IGrantsNeuron>(new NeuronId(Brain, IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
#pragma warning disable CS0618
        await grants.Grant(new Grant { AppId = CapabilityCallers.AssistantApp, Scope = "run", Target = "local/ino/settings", WorkspaceId = "local" });
#pragma warning restore CS0618
        await grants.Grant(new Grant { AppId = "other", Scope = "run", Target = "local/elsewhere", WorkspaceId = "local" });
        var ino = await os.Install<InoApp>();

        await ino.Request("open", new InoOpen());

        Assert.Contains(ino.Side.Entries, entry => entry.Title == "run · local/ino/settings");
        Assert.DoesNotContain(ino.Side.Entries, entry => entry.Title.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OldTurnsAreArchivedNotReplayed()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<RecordingAgent>();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("/scenario morning-brief"));
        var turns = ino.App.State.Turns;
        Assert.NotEmpty(turns);
        Assert.True(string.IsNullOrEmpty(ino.App.State.AgentKey));
        RecordingAgent.Posts.Clear();
        RecordingAgent.Runs = 0;

        await ino.Request("open", new InoOpen());
        await ino.Request("open", new InoOpen());

        Assert.Equal(turns, ino.App.State.Turns);
        var post = Assert.Single(RecordingAgent.Posts);
        Assert.Equal(("Earlier conversation is in the archive.", "migration"), post);
        Assert.Equal(0, RecordingAgent.Runs);
        Assert.False(string.IsNullOrEmpty(ino.App.State.AgentKey));
    }

    [Fact]
    public async Task AFailedMigrationPostIsRetriedOnTheNextOpen()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<RecordingAgent>();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("/scenario morning-brief"));
        RecordingAgent.Posts.Clear();
        RecordingAgent.FailPosts = 1;
        try
        {
            await ino.Request("open", new InoOpen());
            Assert.True(string.IsNullOrEmpty(ino.App.State.AgentKey));

            await ino.Request("open", new InoOpen());

            Assert.Equal(("Earlier conversation is in the archive.", "migration"), Assert.Single(RecordingAgent.Posts));
            Assert.False(string.IsNullOrEmpty(ino.App.State.AgentKey));
        }
        finally { RecordingAgent.FailPosts = 0; }
    }

    [Fact]
    public async Task OnlyInoWritesItsLinks()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        var id = new NeuronId(Brain, "apps.ino-records", IInoRecords.Key);
        var journaled = os.Journal(Brain).Signals(id).Count();

        using (RuntimeCallerContext.Stamp(new CapabilityCaller("p1", "a1", "local", AppId: "local/apps/desktop")))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<IInoRecords>(id).Write(new InoRecords([new InoLink("x", "notes", "page=1")])));
        }

        Assert.Empty((await os.Get<IInoRecords>(id).Read()).Links);
        Assert.Equal(journaled, os.Journal(Brain).Signals(id).Count());
    }

    [Fact]
    public async Task APastedLinkIsInosOwnRecordAcrossRestart()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<RecordingAgent>();
        RecordingAgent.Posts.Clear();
        var ino = await os.Install<InoApp>();
        var request = Guid.NewGuid();

        await ino.Request("link", new InoPasteLink(request, "notes", "page=3", "Notes, page 3"));
        await ino.Request("link", new InoPasteLink(request, "notes", "page=3", "Notes, page 3"));
        await os.Restart();

        var records = await os.Get<IInoRecords>(new NeuronId(Brain, "apps.ino-records", IInoRecords.Key)).Read();
        Assert.Equal(new InoLink(request.ToString("N"), "notes", "page=3"), Assert.Single(records.Links));
        var stored = System.Text.Json.JsonSerializer.Serialize(records.Links, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Equal("page=3", ChatAppCard.FindLink(stored, request)?.Input);
        Assert.Contains(RecordingAgent.Posts, post => post == ("Notes, page 3", ChatAppCard.LinkPostId(request)));
    }

    [Fact]
    public async Task ABadQuerySurfacesInStatus()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        var ino = await os.Install<InoApp>();
        ino.App.FilesVfs = new BrokenVfs();

        await ino.Request("query-db", new InoQueryDb("SELECT a FROM /work/missing.csv"));

        Assert.StartsWith("Could not run the query: disk gone", ino.App.State.Status, StringComparison.Ordinal);
        Assert.Empty(ino.App.State.ActiveCards);
    }

    [Fact]
    public async Task EmptySendIsIgnoredWithoutSendingHello()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend(null, null, null));
        await ino.Request("send", new InoSend("   "));

        Assert.Equal(0, BackgroundAgent.Starts);
        Assert.Equal("New chat", ino.App.State.Title);
        Assert.Null(ino.App.State.Pending);
        Assert.Null(ino.App.State.ActiveRunId);
        Assert.Equal(InoModel.Ready, ino.App.State.Status);
    }

    [Fact]
    public async Task InoLogsItsFailuresFacts()
    {
        Assert.IsType<InoApp>(Activator.CreateInstance(typeof(InoApp)));

        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        var provider = services.BuildServiceProvider();
        var diConstructed = ActivatorUtilities.CreateInstance<InoApp>(provider);
        Assert.NotNull(diConstructed);

        await using var os = TestOS.Local(o => o.Services = provider);
        os.Register(typeof(AppsModule).Assembly);
        os.Register<RecordingAgent>();
        var ino = await os.Install<InoApp>();
        ino.App.FilesVfs = new BrokenVfs();

        await ino.Request("query-db", new InoQueryDb("SELECT a FROM /work/missing.csv"));
        await ino.Request("send", new InoSend("trigger start error"));

        var inoEntries = logs.Entries.Where(e => string.Equals(e.Category, typeof(InoApp).FullName, StringComparison.Ordinal)).ToArray();
        Assert.Contains(inoEntries, e => e.Level == LogLevel.Warning && e.Exception is IOException);
        Assert.Contains(inoEntries, e => e.Level == LogLevel.Error && e.Exception is NotSupportedException);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class RecordingLogger(
            string category,
            List<(string Category, LogLevel Level, Exception? Exception, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add((category, logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class BrokenVfs : IFilesVfsService
    {
        public string BrainId => "local";
        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string? query = null, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<WorkspaceFileEntry?> ResolveFileAsync(string? nameOrUriOrPath, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<string?> ReadTextAsync(string nameOrUriOrPath, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<byte[]?> ReadBytesAsync(string nameOrUriOrPath, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<WorkspaceFileEntry> WriteFileAsync(string fileNameOrPath, ReadOnlyMemory<byte> content, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<WorkspaceFileEntry> WriteTextAsync(string fileNameOrPath, string content, CancellationToken ct = default) => throw new IOException("disk gone");
    }

    [Neuron(AgentSignals.NeuronType)]
    private sealed class RecordingAgent : Neuron, IAgent
    {
        public static List<(string Text, string Id)> Posts { get; } = [];
        public static int Runs { get; set; }
        public static int FailPosts { get; set; }

        public Task Configure(AgentDefinition definition, long expectedRevision) => Task.CompletedTask;

        public async IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Runs++;
            await Task.Yield();
            yield return new AgentTurnEvent.Started("run-1");
        }

        public Task<string> Start(AgentTurn turn, string? flowId = null) => throw new NotSupportedException();
        public Task Cancel() => Task.CompletedTask;
        public Task CancelRun(string runId) => Task.CompletedTask;
        public Task<AgentState> Read() => Task.FromResult(new AgentState(1, new AgentDefinition(), null, null, []));
        public Task<IntoChat.OS.Modules.AI.Inference.AiMessage[]> Transcript() => Task.FromResult<IntoChat.OS.Modules.AI.Inference.AiMessage[]>([]);
        public Task Clear() => Task.CompletedTask;
        public Task Post(string text, string id)
        {
            if (FailPosts > 0) { FailPosts--; throw new InvalidOperationException("Post failed."); }
            Posts.Add((text, id));
            return Task.CompletedTask;
        }
    }

    [Neuron(AgentSignals.NeuronType)]
    private sealed class BackgroundAgent : Neuron, IAgent
    {
        public static int Starts { get; set; }
        public static int Runs { get; set; }
        public static int Cancels { get; set; }
        public static string? Reply { get; set; }
        public static string? LastConfiguredModel { get; set; }
        public static string? LastTurnModel { get; set; }
        private static long _revision;
        private static AgentDefinition _definition = new();

        public static void Reset()
        {
            Starts = 0;
            Runs = 0;
            Cancels = 0;
            Reply = null;
            LastConfiguredModel = null;
            LastTurnModel = null;
            _revision = 0;
            _definition = new();
        }

        public Task Configure(AgentDefinition definition, long expectedRevision)
        {
            _definition = definition;
            _revision++;
            LastConfiguredModel = definition.Model?.Model;
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default)
        {
            Runs++;
            throw new NotSupportedException("Ino starts runs in the background.");
        }

        public Task<string> Start(AgentTurn turn, string? flowId = null)
        {
            Starts++;
            LastTurnModel = turn.Model?.Model ?? _definition.Model?.Model;
            return Task.FromResult("run-1");
        }

        public Task Cancel() { Cancels++; return Task.CompletedTask; }
        public Task CancelRun(string runId) => Task.CompletedTask;
        public Task<AgentState> Read() => Task.FromResult(new AgentState(_revision, _definition, null,
            Starts > 0 ? new AgentRun("run-1", AgentRunStatus.Running, "hello", DateTimeOffset.UtcNow) : null, []));
        public Task<IntoChat.OS.Modules.AI.Inference.AiMessage[]> Transcript()
            => Task.FromResult<IntoChat.OS.Modules.AI.Inference.AiMessage[]>(
                Reply is null ? [] : [new("assistant", [new IntoChat.OS.Modules.AI.Inference.AiText(Reply)])]);
        public Task Clear() => Task.CompletedTask;
        public Task Post(string text, string id) => Task.CompletedTask;
    }

    [Neuron(IChat.Type)]
    private sealed class BackgroundChat : Neuron, IChat
    {
        private static long _revision;
        private static ChatState _state = new();
        public static long Revision => _revision;

        public static void Reset()
        {
            _revision = 0;
            _state = new();
        }

        public static void Complete(string agentKey, string runId, string userText, string reply)
        {
            _revision++;
            _state = new ChatState
            {
                Name = "local/ino/chats/main",
                Revision = _revision,
                Selection = new ChatSelection(agentKey, 1),
                Projection = new ChatProjection(agentKey, 1, _revision,
                [
                    new ChatEntry($"{runId}/user", ChatRole.User, userText),
                    new ChatEntry($"{runId}/assistant", ChatRole.Assistant, reply),
                ]),
                Run = new ChatRunSnapshot(agentKey, 1, _revision, runId, ChatRunStatus.Completed),
            };
        }

        public Task<ChatSelection> SelectAgent(string agent, long expectedSelectionRevision, Guid operationId)
        {
            _revision++;
            var selection = new ChatSelection(agent, expectedSelectionRevision + 1);
            _state = _state with
            {
                Name = $"{Id.Brain.Value}/{Id.Key}",
                Revision = _revision,
                Selection = selection,
            };
            return Task.FromResult(selection);
        }

        public Task Project(ChatProjection projection)
        {
            _revision++;
            _state = _state with { Revision = _revision, Projection = projection };
            return Task.CompletedTask;
        }

        public Task Activate(string source, long generation, string actionId) => Task.CompletedTask;
        public Task MarkRead(string source, long generation, long version, string[] messageIds) => Task.CompletedTask;
        public Task<ChatState> Read() => Task.FromResult(_state);
    }

    [Neuron("ui.compute")]
    private sealed class FakeCompute : Neuron, IntoChat.OS.Modules.Flutter.Compute.ICompute
    {
        public static List<(string Key, long Micros)> Records { get; } = [];
        public static void Reset() => Records.Clear();
        public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> Read() => Task.FromResult(new IntoChat.OS.Modules.Flutter.Compute.ComputeState(0, DateTimeOffset.UtcNow, []));
        public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> SetBudget(long monthlyBudgetMicros) => Task.FromResult(new IntoChat.OS.Modules.Flutter.Compute.ComputeState(0, DateTimeOffset.UtcNow, [], monthlyBudgetMicros));
        public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> Record(string key, long micros)
        {
            Records.Add((key, micros));
            return Read();
        }
    }
}
