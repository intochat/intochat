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
                // The smart toggle writes a setting, which the profile writer copies into the agent's context.
                os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
            },
            cancellationToken: ct);
        Assert.True(inoReport.Passed);
        Assert.Single(inoReport.Scenarios);

        var postReport = await BddRunner.VerifyAsync<PostApp>(fakes: os => os.Register(typeof(AppsModule).Assembly), cancellationToken: ct);
        Assert.True(postReport.Passed);
        Assert.Single(postReport.Scenarios);

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
        Assert.Equal(started.RunId, ino.App.State.ActiveRunId);
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
        Assert.Equal("Thinking", ino.App.State.Status);
        Assert.Equal(["ino/conversations/main"], await os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey)).Current());
    }

    [Fact]
    public async Task ToolCallProgressSetsStatusUsingTool()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));

        await Deliver(ino, new TurnProgressed("ino/conversations/main", "run-1", "tool-call", null, "neuron_call", "{}", 1));

        Assert.Equal("Using neuron_call", ino.App.State.Status);
    }

    [Fact]
    public async Task CardProgressAppendsOneCard()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        var card = new InoCard("settings-models", "settings", "Settings · models", "Open settings", "neuron://local/apps/settings", "ready");
        var web = System.Text.Json.JsonSerializerOptions.Web;

        // The agent reports a card twice, as the tool result and as its card step: Ino shows it once.
        await Deliver(ino, new TurnProgressed("ino/conversations/main", "run-1", "tool-result", null, "neuron_call", System.Text.Json.JsonSerializer.Serialize(new InoToolResult("Opened settings.", card), web), 2));
        await Deliver(ino, new TurnProgressed("ino/conversations/main", "run-1", "card", null, "neuron_call", System.Text.Json.JsonSerializer.Serialize(card, web), 3));

        Assert.Equal(card, Assert.Single(ino.App.State.ActiveCards));
    }

    [Fact]
    public async Task DuplicateProgressIsIgnored()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        var card = new InoCard("c", "settings", "Settings", "Open", "neuron://local/apps/settings", "ready");
        var payload = System.Text.Json.JsonSerializer.Serialize(new InoToolResult("ok", card), System.Text.Json.JsonSerializerOptions.Web);
        var step = new TurnProgressed("ino/conversations/main", "run-1", "tool-result", null, "neuron_call", payload, 2);

        await Deliver(ino, step);
        await Deliver(ino, step);

        Assert.Single(ino.App.State.ActiveCards);
        Assert.Equal(["run-1:2"], ino.App.State.SeenProgress ?? []);
    }

    [Fact]
    public async Task TurnCompletedReloadsTranscript()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        BackgroundAgent.Reply = "Hi from the background";

        await Deliver(ino, new TurnCompleted("ino/conversations/main", "run-1", "Hi from the background"));

        Assert.Equal("Ready", ino.App.State.Status);
        Assert.Null(ino.App.State.ActiveRunId);
        Assert.Contains("Hi from the background", Strings(ino.App.Render()));
    }

    [Fact]
    public async Task AgentBlockedPauses()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));

        await Deliver(ino, new AgentBlocked("ino/conversations/main", true));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal("Paused", ino.App.State.Status);
    }

    [Fact]
    public async Task StopCancelsActiveRun()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Contains("Stop", Strings(ino.App.Render()));

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
        Assert.Contains("Smart mode: on", Strings(ino.App.Render()));
    }

    private static TestOS BackgroundOs()
    {
        BackgroundAgent.Reset();
        var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<BackgroundAgent>();
        return os;
    }

    // TestOS does not serve an app's declared watches: hand the signal to the set watch that takes it, as the host does.
    private static Task Deliver<TSignal>(InstalledApp<InoApp> ino, TSignal signal) where TSignal : Signal
        => Assert.Single(ino.App.DeclaredWatches(), watch => watch.SignalType == typeof(TSignal) && watch.IsSet).Handle(signal);

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

    [Fact]
    public async Task ACancelledRunClearsTheActiveRun()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));

        await Deliver(ino, new TurnFailed("ino/conversations/main", "run-1", "cancelled", Cancelled: true));

        Assert.Null(ino.App.State.ActiveRunId);
        Assert.Equal("Cancelled", ino.App.State.Status);
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

    // An agent that counts Post calls and Run starts.
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

    // An agent whose Start hands the run off and returns at once; the test delivers the run's signals.
    [Neuron(AgentSignals.NeuronType)]
    private sealed class BackgroundAgent : Neuron, IAgent
    {
        public static int Starts { get; set; }
        public static int Runs { get; set; }
        public static int Cancels { get; set; }
        public static string? Reply { get; set; }

        public static void Reset() { Starts = 0; Runs = 0; Cancels = 0; Reply = null; }

        public Task Configure(AgentDefinition definition, long expectedRevision) => Task.CompletedTask;

        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default)
        {
            Runs++;
            throw new NotSupportedException("Ino starts runs in the background.");
        }

        public Task<string> Start(AgentTurn turn, string? flowId = null) { Starts++; return Task.FromResult("run-1"); }
        public Task Cancel() { Cancels++; return Task.CompletedTask; }
        public Task CancelRun(string runId) => Task.CompletedTask;
        public Task<AgentState> Read() => Task.FromResult(new AgentState(1, new AgentDefinition(), null,
            Starts > 0 ? new AgentRun("run-1", AgentRunStatus.Running, "hello", DateTimeOffset.UtcNow) : null, []));
        public Task<IntoChat.OS.Modules.AI.Inference.AiMessage[]> Transcript()
            => Task.FromResult<IntoChat.OS.Modules.AI.Inference.AiMessage[]>(
                Reply is null ? [] : [new("assistant", [new IntoChat.OS.Modules.AI.Inference.AiText(Reply)])]);
        public Task Clear() => Task.CompletedTask;
        public Task Post(string text, string id) => Task.CompletedTask;
    }
}
