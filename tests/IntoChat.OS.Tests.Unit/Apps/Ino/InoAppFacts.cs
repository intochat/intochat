using System.Reflection;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoAppFacts
{
    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private static IServiceProvider WithRedactor(IServiceProvider? parent = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRedactor, NoSecrets>();
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
        Assert.Contains(typeof(IStudio).FullName!, def.Contracts);
        Assert.Contains(typeof(IMemory).FullName!, def.Contracts);
        Assert.Contains(typeof(IAspire).FullName!, def.Contracts);
        Assert.Contains(typeof(ITraceReader).FullName!, def.Contracts);
        Assert.Contains(typeof(IFlows).FullName!, def.Contracts);
        Assert.Contains("IntoChat.OS.Modules.Microsoft.Roslyn.ICodeGraph", def.Contracts);
        Assert.Contains("`studio_propose_change`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`flows_start_engineer_run`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`flows_inspect_engineer_run`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`studio_create_issue_triage_synapse`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`github_repository_dismiss_issue_draft`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`ai_memory_remember_preference`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`aspire_get_traces_async`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`roslyn_codegraph_search_symbols`", def.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("IntoChat.OS.Modules.Roslyn.ICodeGraph", def.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void InoPersonaShowsHowToDrawANodeCardAndHowToPoint()
    {
        var def = InoAgentDefinition.For(new UserSettingsState() with { DisplayName = "Vlad", DefaultModel = "qwen2.5-coder:14b" });
        const string expectedNode = """{"kind":"dataTable","title":"Fruit","columns":["Fruit","Colour"],"rows":[["Apple","Red"],["Banana","Yellow"]]}""";

        Assert.Contains("`focus_show`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains(expectedNode, def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`focus_point`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("only points at something that already exists", def.Instructions, StringComparison.Ordinal);

        // The node example in the persona must itself be valid JSON accepted by InoNodeCard.
        Assert.True(InoNodeCard.TryRead(expectedNode, out var node, out var refusal), refusal);
        Assert.IsType<DataTable>(node);
    }

    [Fact]
    public void PersonaTeachesNoArgumentArrays()
    {
        var text = InoAgentDefinition.For(new()).Instructions;
        Assert.DoesNotContain("neurons_call", text, StringComparison.Ordinal);
        Assert.DoesNotContain("arguments `[", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IIno", text, StringComparison.Ordinal);
        Assert.True(text.Length <= 4_000, $"Persona is {text.Length} chars (budget <= 4000).");
    }

    [Fact]
    public void EveryToolThePersonaNamesExists()
    {
        var names = IntoChat.OS.Tests.Unit.Apps.Ino.InoToolBudgetFacts.InoTools()
            .Select(static t => t.Name)
            .Concat(["neurons_search", "neurons_list", "neurons_context"])
            .ToHashSet(StringComparer.Ordinal);
        var named = System.Text.RegularExpressions.Regex.Matches(
                InoAgentDefinition.For(new()).Instructions,
                @"`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`")
            .Select(static m => m.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(named);
        Assert.All(named, n => Assert.Contains(n, names));
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
        Assert.Null(photoCraftInstalled.App.State.WebUrl);
        await photoCraftInstalled.Request("remove-background", new PhotoCraftRemoveBackground("/work/hero.png", "native-linux"));
        Assert.Contains("not connected", photoCraftInstalled.App.State.Status, StringComparison.Ordinal);
        Assert.Contains(photoCraftInstalled.Main.Texts, text => text.Contains("not connected", StringComparison.Ordinal));
        Assert.Empty(photoCraftInstalled.App.State.History);

        var filmCraftInstalled = await os.Install<FilmCraftApp>();
        Assert.Null(filmCraftInstalled.App.State.WebUrl);
        await filmCraftInstalled.Request("export", new FilmCraftExport("h264-1080p60", "/work/clip.fcproj", "native-linux"));
        Assert.Contains("not connected", filmCraftInstalled.App.State.Status, StringComparison.Ordinal);
        Assert.Contains(filmCraftInstalled.Main.Texts, text => text.Contains("not connected", StringComparison.Ordinal));

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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
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

        await ino.Request("send", new InoSend("/model claude-sonnet-4-6"));
        Assert.Equal("Switched model to claude-sonnet-4-6", ino.App.State.Status);
        await os.Idle();
        Assert.Contains("claude-sonnet-4-6", ino.App.State.Status, StringComparison.Ordinal);
        Assert.Equal("claude-sonnet-4-6", (await userSettings.Read()).DefaultModel);
        Assert.Equal("claude-sonnet-4-6", BackgroundAgent.LastConfiguredModel);

        await ino.Request("send", new InoSend("turn 2"));
        Assert.Equal("claude-sonnet-4-6", BackgroundAgent.LastTurnModel);
        BackgroundChat.Complete("local/ino/conversations/main", "run-1", "turn 2", "reply 2");
        await Deliver(ino, new ChatRunChanged("local/ino/chats/main", BackgroundChat.Revision, ChatRunStatus.Completed));
        BackgroundAgent.Starts = 0;

        await settingsApp.Request("save-preferences", new SettingsSavePreferences(
            DisplayName: "Ada Lovelace",
            Theme: "dark",
            DefaultModel: "gpt-5.4",
            StylePreset: "fluent-acrylic",
            BarLayout: "floating-glass"));
        await os.Idle();
        Assert.Equal("gpt-5.4", (await userSettings.Read()).DefaultModel);
        Assert.Equal("gpt-5.4", BackgroundAgent.LastConfiguredModel);

        await ino.Request("send", new InoSend("turn 3"));
        Assert.Equal("gpt-5.4", BackgroundAgent.LastTurnModel);

        await using var agentOs = AgentOs();
        var liveIno = await agentOs.Install<InoApp>();
        var liveProfile = agentOs.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/user.profile"));
        var liveAgent = agentOs.Get<IAgent>(AgentOf("main"));
        await liveIno.Request("send", new InoSend("/model claude-sonnet-4-6"));
        await agentOs.Idle();
        Assert.Equal("claude-sonnet-4-6", (await liveAgent.Read()).Definition.Model?.Model);
        Assert.Equal("claude-sonnet-4-6", liveIno.App.State.SelectedModel);
        // ProfileContextWriter never writes an "inference" model entry into user.profile.
        Assert.DoesNotContain((await liveProfile.Read()).Entries, e => e.Name == "inference");
    }

    [Fact]
    public async Task OpenComputesCanDictateWithoutPersistingIt()
    {
        FakeSpeechRecognizer.AvailableResult = true;
        try
        {
            await using var os = BackgroundOs();
            os.Register<FakeSpeechRecognizer>();
            var ino = await os.Install<InoApp>();
            await ino.Request("open", new InoOpen());

            Assert.True(ino.App.CanDictate);
            Assert.True(InoDrawing.PropsOf(ino.App.Render()).Dictation);
            Assert.Null(typeof(InoAppState).GetProperty("CanDictate"));

            FakeSpeechRecognizer.AvailableResult = false;
            await ino.Request("open", new InoOpen());
            Assert.False(ino.App.CanDictate);
            Assert.False(InoDrawing.PropsOf(ino.App.Render()).Dictation);
        }
        finally
        {
            FakeSpeechRecognizer.AvailableResult = false;
        }
    }

    [Fact]
    public async Task ChoosingStockChatModelInSettingsIsPreservedWhenCoderIsDetectedLocally()
    {
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();

        try
        {
            var ino = await os.Install<InoApp>();
            var settingsApp = await os.Install<SettingsApp>();

            await settingsApp.Request("save-preferences", new SettingsSavePreferences(
                DisplayName: "User",
                Theme: "dark",
                DefaultModel: "qwen3.5:4b",
                StylePreset: "fluent-acrylic",
                BarLayout: "floating-glass"));
            await os.Idle();

            await ino.Request("send", new InoSend("chat with default"));
            Assert.Equal("qwen3.5:4b", BackgroundAgent.LastTurnModel);
            Assert.Equal("qwen3.5:4b", ino.App.State.SelectedModel);
        }
        finally
        {
            FakeModelCatalog.Reset();
        }
    }

    [Fact]
    public async Task MissingOllamaUnderLocalTopologySurfacesNoProviderSetupCard()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();
        FakeModelCatalog.PickedChat = KnownModels.Qwen35.Id;

        try
        {
            var ino = await os.Install<InoApp>();
            await ino.Request("send", new InoSend("hello without ollama"));
            BackgroundChat.Fail("local/ino/conversations/main", "run-1", "hello without ollama", "provider-unavailable");
            await Deliver(ino, new ChatRunChanged("local/ino/chats/main", BackgroundChat.Revision, ChatRunStatus.Failed));

            Assert.Equal("no-provider", ino.App.State.Error?.Code);
            Assert.True(InoView.ShouldShowModelSetup(ino.App.State));
        }
        finally
        {
            FakeModelCatalog.Reset();
        }
    }

    [Fact]
    public async Task NewConversationMovesTheWatchBeforeTheNextSend()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        BackgroundAgent.Reply = "Hi from the background";
        BackgroundChat.Complete("local/ino/conversations/main", "run-1", "hello", "Hi from the background");

        await Deliver(ino, new ChatRunChanged("local/ino/chats/main", BackgroundChat.Revision, ChatRunStatus.Completed));

        Assert.Equal(InoModel.Ready, ino.App.State.Status);
        Assert.Null(ino.App.State.ActiveRunId);
        Assert.Contains("Hi from the background", Texts(InoDrawing.Screen(ino.App)));
        Assert.Equal(("ino", 1_000L), FakeCompute.Records.Single());
    }

    [Fact]
    public async Task A_raw_send_while_a_reply_runs_is_refused_and_starts_nothing()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Equal("run-1", ino.App.State.ActiveRunId);

        // Ino's own not_busy rule: the request reaches Ino while the run is active and is refused.
        await ino.Request("send", new InoSend("second question"));

        Assert.Equal(1, BackgroundAgent.Starts);
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        Assert.NotEqual("second question", ino.App.State.Pending?.Text);
        Assert.DoesNotContain(ino.App.Messages, m => m.Text == "second question");
    }

    [Fact]
    public async Task A_second_send_through_the_thread_while_a_reply_runs_starts_nothing()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
        var view = await thread.Read();

        // Ino said it cannot take a message: the thread refuses the text in order, before it could reach Ino.
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => thread.Activate(view!.Alias, view.Generation, "composer", "second question"));
        await os.Idle();

        Assert.Equal(ControlProps.Disabled, refused.Reason);
        Assert.Equal(1, BackgroundAgent.Starts);
        Assert.DoesNotContain(ino.App.Messages, m => m.Text == "second question");
    }

    [Fact]
    public async Task ChoosingSwitchesOnlyThisConversation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();
        FakeModelCatalog.Reset();

        try
        {
            var ino = await os.Install<InoApp>();
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.SelectModelKey, "gpt-5.4");
            await os.Idle();

            Assert.Equal("gpt-5.4", ino.App.State.SelectedModel);
            var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
            Assert.Null(settings.RoleOf(Role.Chat));
        }
        finally
        {
            FakeModelCatalog.Reset();
        }
    }

    [Fact]
    public async Task MakeDefaultPinsTheChatRole()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();
        FakeModelCatalog.Reset();

        try
        {
            var ino = await os.Install<InoApp>();
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.PinModelKey, "gpt-5.4");
            await os.Idle();

            Assert.Equal("gpt-5.4", ino.App.State.SelectedModel);
            var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
            Assert.Equal("gpt-5.4", settings.RoleOf(Role.Chat)!.Value.Value);
        }
        finally
        {
            FakeModelCatalog.Reset();
        }
    }

    [Fact]
    public async Task ChoosingANotInstalledModelInstallsIt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-ino-device-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor>(new NoSecrets())
            .AddSingleton(machine)
            .BuildServiceProvider();

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        FakeModelCatalog.Reset();
        FakeDevice.Reset(machine.Value);
        try
        {
            await using var os = TestOS.Local(o => o.Services = services);
            os.Register(typeof(AppsModule).Assembly);
            os.Register<BackgroundAgent>();
            os.Register<BackgroundChat>();
            os.Register<FakeModelCatalog>();
            os.Register<FakeDevice>();

            var ino = await os.Install<InoApp>();
            var previousModel = ino.App.State.SelectedModel;
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.SelectModelKey, KnownModels.Gemma4.Id.Value);
            await os.Idle();

            Assert.Equal(KnownModels.Gemma4.Id, Assert.Single(FakeDevice.Installs));
            Assert.Empty(FakeDevice.Uses);
            Assert.Equal(previousModel, ino.App.State.SelectedModel);
        }
        finally
        {
            FakeModelCatalog.Reset();
            FakeDevice.Reset(string.Empty);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task MakeDefaultOnANotInstalledModelUsesIt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-ino-device-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor>(new NoSecrets())
            .AddSingleton(machine)
            .BuildServiceProvider();

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        FakeModelCatalog.Reset();
        FakeDevice.Reset(machine.Value);
        try
        {
            await using var os = TestOS.Local(o => o.Services = services);
            os.Register(typeof(AppsModule).Assembly);
            os.Register<BackgroundAgent>();
            os.Register<BackgroundChat>();
            os.Register<FakeModelCatalog>();
            os.Register<FakeDevice>();

            var ino = await os.Install<InoApp>();
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.PinModelKey, KnownModels.Gemma4.Id.Value);
            await os.Idle();

            Assert.Equal((Role.Chat, KnownModels.Gemma4.Id), Assert.Single(FakeDevice.Uses));
            Assert.Empty(FakeDevice.Installs);
            var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
            Assert.Null(settings.RoleOf(Role.Chat));
        }
        finally
        {
            FakeModelCatalog.Reset();
            FakeDevice.Reset(string.Empty);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task InstallChangedRedrawsThePickerWithProgress()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-ino-device-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor>(new NoSecrets())
            .AddSingleton(machine)
            .BuildServiceProvider();

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        FakeModelCatalog.Reset();
        FakeModelCatalog.ListedCards = [KnownModels.Qwen35, KnownModels.Gemma4];
        FakeModelCatalog.ListedAvailability =
        [
            new(KnownModels.Qwen35.Id, true, null),
            new(KnownModels.Gemma4.Id, false, "Not installed"),
        ];
        FakeDevice.Reset(machine.Value);
        try
        {
            await using var os = TestOS.Local(o => o.Services = services);
            os.Register(typeof(AppsModule).Assembly);
            os.Register<BackgroundAgent>();
            os.Register<BackgroundChat>();
            os.Register<FakeModelCatalog>();
            os.Register<FakeDevice>();

            var ino = await os.Install<InoApp>();
            FakeDevice.QueueEntries =
            [
                new Install(KnownModels.Gemma4.Id, RuntimeId.Ollama, InstallState.Downloading, 42, 420, 1000, null, null),
            ];

            await os.Get<IDevice>(new NeuronId(Brain, "devices.device", $"devices/{machine.Value}")).Install(KnownModels.Gemma4.Id);
            await os.Idle();

            var props = InoDrawing.PropsOf(ino.App.Render());
            var gemma = Assert.Single(props.Models!, static m => m.Id == KnownModels.Gemma4.Id.Value);
            Assert.Equal("Downloading 42%", gemma.Note);
            Assert.Equal(0.42, gemma.Progress);
        }
        finally
        {
            FakeModelCatalog.Reset();
            FakeDevice.Reset(string.Empty);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task AgentBlockedPauses()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));

        await Deliver(ino, new AgentBlocked("ino/conversations/main", true));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal(InoModel.Paused, ino.App.State.Status);
        Assert.Contains("Ino is paused.", Texts(InoDrawing.Screen(ino.App)));
    }

    [Fact]
    public async Task StopCancelsActiveRun()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Contains("Stop", Texts(InoDrawing.Screen(ino.App)));

        await ino.Request("stop", new InoStop());

        Assert.Equal(1, BackgroundAgent.Cancels);
    }

    [Fact]
    public async Task SmartToggleWritesVariable()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
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
        var view = InoDrawing.Screen(state);
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

        var initialTexts = Texts(InoDrawing.Screen(ino.App));
        Assert.All(initialTexts, text =>
        {
            Assert.DoesNotContain("neuron://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("| scope:", text, StringComparison.OrdinalIgnoreCase);
        });

        await ino.Request("send", new InoSend("/scenario morning-brief"));
        var scenarioTexts = Texts(InoDrawing.Screen(ino.App));
        Assert.All(scenarioTexts, text =>
        {
            Assert.DoesNotContain("neuron://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("| scope:", text, StringComparison.OrdinalIgnoreCase);
        });

        await ino.Request("make", new InoMake("Build calculator", ["calculator"]));
        var makeTexts = Texts(InoDrawing.Screen(ino.App));
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
            using var _ = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Kernel(Brain));
            await focus.Set(new("stop", "maintenance window", [], [ContextTags.Interrupt]));
        }, TestContext.Current.CancellationToken);

        await ino.Request("send", new InoSend("hello"));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal(InoModel.Paused, ino.App.State.Status);
        var c = InoDrawing.Conversation(ino.App.State);
        Assert.NotNull(c.Error);
        Assert.Equal("Ino is paused.", c.Error!.What);
        Assert.Contains("maintenance window", c.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSidePanelListsAgentGrants()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = AgentOs();
        os.Register<GrantsNeuron>();
        var grants = os.Get<IGrantsNeuron>(new NeuronId(Brain, IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
        await grants.Grant(new Grant { AppId = IntoChat.OS.Modules.Identity.Contracts.Grants.CallerApps.Assistant, Scope = "run", Target = "local/ino/settings", WorkspaceId = "local" });
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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        var id = new NeuronId(Brain, "apps.ino-records", IInoRecords.Key);
        var journaled = os.Journal(Brain).Signals(id).Count();

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/desktop", new Actor.Person("p1", "a1"), new BrainId("local"))))
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

    [Fact]
    public async Task InoAppUsesInjectedTimeProviderAndExtractsModelSetup()
    {
        var fixedNow = new DateTimeOffset(2026, 11, 16, 12, 30, 0, TimeSpan.Zero);
        var fixedClock = new FixedTimeProvider(fixedNow);

        var setupType = typeof(InoApp).Assembly.GetType("IntoChat.OS.Apps.Ino.InoModelSetup", throwOnError: false);
        Assert.NotNull(setupType);

        var services = new ServiceCollection();
        services.AddSingleton<ISecretRedactor>(new NoSecrets());
        services.AddSingleton<TimeProvider>(fixedClock);
        var provider = services.BuildServiceProvider();

        BackgroundAgent.Reset();
        BackgroundChat.Reset();
        try
        {
            await using var os = TestOS.Local(o => o.Services = provider);
            os.Register(typeof(AppsModule).Assembly);
            os.Register<BackgroundAgent>();
            os.Register<BackgroundChat>();
            var ino = await os.Install<InoApp>();

            await ino.Request("send", new InoSend("check clock"));
            Assert.Equal(fixedNow, ino.App.State.Pending?.At);
        }
        finally
        {
            BackgroundAgent.Reset();
            BackgroundChat.Reset();
        }
    }

    [Fact]
    public void InoAppState_AggregateExplorer_UnfoldsHealthyStateGraphAndRuleGuards()
    {
        var instanceFields = typeof(InoApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(instanceFields);

        var graph = AggregateExplorer<InoAppState>.ExploreScreen<InoApp>(maxStates: 48).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);
        Assert.Contains(graph.States, s => s.State.ActiveSection == "approvals");
        Assert.Contains(graph.States, s => s.State.ConversationId == "c-0123456789abcdef0123456789abcdef");
        Assert.Contains(graph.States, s => s.State.Pending is not null && s.State.Status == InoModel.Working);
        Assert.Contains(graph.States, s => s.State.SelectedModel == "qwen2.5-coder:14b");
        Assert.Contains(graph.States, s => s.State.Approvals.Any(a => a.PlanId == "plan-1"));
        Assert.Contains(graph.States, s => s.State.DevUiMode);

        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_section", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_thread_id", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("already_on_thread", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("non_empty_prompt", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("not_busy", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_model", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("unsupported_provider", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_provider_credential", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_approval_plan", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("approval_not_found", StringComparer.Ordinal));

        var oversizedHistory = Enumerable.Range(1, 80).Select(i => $"ino/conversations/c-{i:D4}").ToArray();
        var boundedState = new InoAppState() with { RecentConversations = oversizedHistory };
        Assert.Equal(InoAppState.MaxRecentConversations, boundedState.RecentConversations.Length);
        Assert.True(boundedState.BoundedThreadHistoryInvariant);
        Assert.True(boundedState.NonNullCollectionsInvariant);

        var engineerState = new InoAppState() with
        {
            Cards =
            [
                new InoCard("flow-eng-1", "engineer", "Add Clamp helper", "2 worktrees verified", "neuron://local/apps/engineer#flow-eng-1", "waiting-approval", Columns: ["os", "2x LocalGit", "approval-gate"]),
            ],
        };
        var engineerPreview = AggregateExplorer<InoAppState>.PreviewNode<InoApp>(engineerState);
        var engineerWire = System.Text.Json.JsonSerializer.Serialize(engineerPreview);
        Assert.Contains("Open Engineer Workbench", engineerWire, StringComparison.Ordinal);

        Assert.Contains("digraph InoAppState", graph.ToDot(), StringComparison.Ordinal);
        Assert.Contains(InoApp.Bundle.Manifest.Operations, op => op.Name == UiCommand.SignalAlias);
    }

    [Fact]
    public async Task InoApp_DomainRules_AndUiIntrospector_UnfoldCleanAccordantStateGraphAndDot()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var ino = await os.Install<InoApp>();

        await ino.Request("switch-conversation", new InoSwitchConversation(""));
        Assert.Contains("invalid_thread_id", ino.App.LastRejection!.Rules);

        await ino.Request("switch-conversation", new InoSwitchConversation("main"));
        Assert.Contains("already_on_thread", ino.App.LastRejection!.Rules);

        await ino.Request("send", new InoSend("   "));
        Assert.Contains("non_empty_prompt", ino.App.LastRejection!.Rules);

        await ino.Request("use-local", new InoUseLocalModel(Model: "bad model name"));
        Assert.Contains("invalid_model", ino.App.LastRejection!.Rules);

        await ino.Request("save-provider", new InoSaveProvider(Value: "bad", Provider: "openai"));
        Assert.Contains("invalid_provider_credential", ino.App.LastRejection!.Rules);

        await ino.Request("save-provider", new InoSaveProvider(Value: "sk-valid-secret-1234", Provider: "unknown"));
        Assert.Contains("unsupported_provider", ino.App.LastRejection!.Rules);

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(Brain));
        var snapshot = await introspector.ExploreStateGraph(typeof(InoApp).FullName!, maxStates: 48);

        Assert.True(snapshot.IsHealthy, string.Join("\n", snapshot.Violations));
        Assert.Empty(snapshot.Violations);
        Assert.True(snapshot.States.Length >= 6);
        Assert.Contains(snapshot.Transitions, t => t.Accepted);
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("non_empty_prompt", StringComparer.Ordinal));
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("already_on_thread", StringComparer.Ordinal));
        Assert.Contains("digraph InoAppState", snapshot.Dot, StringComparison.Ordinal);

        var report = await introspector.SyncContext();
        Assert.Contains("InoApp", report.ContextSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoApp_HandlesINavigatedTo_Prompt_ConversationId_FocusTarget_AndEnforcesINavigatedFromRuleGuard()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        var screen = os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default"));

        // 1. Typed INavigatedTo<InoNavArgs> switches conversation, records ActiveFocusTarget, seeds DraftPrompt + ChatThreadProps, and stamps ActiveTransition
        var navPayload = System.Text.Json.JsonSerializer.Serialize(
            new InoNavArgs(
                Prompt: "Triage GitHub issue #42",
                ConversationId: "conv-triage-42",
                FocusTarget: "node:app.tasks"),
            System.Text.Json.JsonSerializerOptions.Web);
        await screen.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    ArgsJson: navPayload,
                    FromNeuron: "app.tasks",
                    Transition: ViewTransition.SharedAxisX),
                typeof(UiNavigateTo))));
        await os.Idle();

        Assert.Equal(ViewTransition.SharedAxisX, ino.App.ActiveTransition);
        Assert.Equal("conv-triage-42", ino.App.State.ConversationId);
        Assert.Equal("Triage GitHub issue #42", ino.App.State.DraftPrompt);
        Assert.Equal("node:app.tasks", ino.App.State.ActiveFocusTarget);

        var threadProps = InoDrawing.PropsOf(ino.App.Render());
        Assert.Equal("Ask Ino: Triage GitHub issue #42", threadProps.Placeholder);
        Assert.Equal("Ready to ask: Triage GitHub issue #42", threadProps.EmptyText);

        var arrivedView = (await screen.Open())!;
        var arrivedRaw = arrivedView.View.GetRawText();
        Assert.Contains("Ask Ino", arrivedRaw, StringComparison.Ordinal);
        Assert.Contains("Tasks", arrivedRaw, StringComparison.Ordinal);
        Assert.Contains(new PresentedView("main", ino.App.Render()).Nodes.OfType<TagPill>(), static p => p.Label == "Context · Tasks");
        Assert.Contains("Triage GitHub issue #42", arrivedRaw, StringComparison.Ordinal);
        Assert.Contains(InoView.SendDraftKey, arrivedRaw, StringComparison.Ordinal);
        Assert.Contains(InoView.DismissDraftKey, arrivedRaw, StringComparison.Ordinal);

        // 2. Clicking Send Prompt dispatches the seeded prompt and clears DraftPrompt
        await screen.Activate("app.ino", arrivedView.Generation, InoView.SendDraftKey);
        await os.Idle();
        Assert.Equal(1, BackgroundAgent.Starts);
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        Assert.Null(ino.App.State.DraftPrompt);

        // 3. Normal multitasking departure while Ino works in the background is never blocked
        await screen.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.tasks", Route: "approvals", Force: false),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.Null(ino.App.LastRejection);

        // 4. Unsaved provider credential setup draft blocks unforced departure with Rule:ino_setup_unsaved, while Force=true bypasses and clears it
        await screen.Dispatch(new Command(
            ".",
            UiCommand.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
                new UiCommand("state", nameof(InoAppState.SetProviderSetupDraft), [System.Text.Json.JsonSerializer.SerializeToElement(true)]),
                typeof(UiCommand))));
        await os.Idle();
        Assert.True(ino.App.State.HasUnsavedProviderDraft);

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Dispatch(new Command(
                ".",
                UiNavigateFrom.SignalAlias,
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
                    new UiNavigateFrom(ToNeuron: "app.settings", Route: "connections/github", Force: false),
                    typeof(UiNavigateFrom)))));
        Assert.StartsWith("Rule:ino_setup_unsaved:", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Finish or discard provider credential setup before leaving.", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Finish or discard provider credential setup before leaving.", (await screen.Open())!.View.GetRawText(), StringComparison.Ordinal);

        await screen.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.settings", Route: "connections/github", Force: true),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.False(ino.App.State.HasUnsavedProviderDraft);
        Assert.Null(ino.App.LastRejection);

        // 5. Plain input coercion via UiNavigateTo.Input seeds DraftPrompt, and Dismiss clears DraftPrompt & ActiveFocusTarget
        await screen.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    FromNeuron: "app.engineer",
                    Transition: ViewTransition.FadeThrough,
                    Input: "Explain failing PR check"),
                typeof(UiNavigateTo))));
        await os.Idle();
        Assert.Equal("Explain failing PR check", ino.App.State.DraftPrompt);

        var seededAgain = (await screen.Open())!;
        await screen.Activate("app.ino", seededAgain.Generation, InoView.DismissDraftKey);
        await os.Idle();
        Assert.Null(ino.App.State.DraftPrompt);
        Assert.Null(ino.App.State.ActiveFocusTarget);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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

        public static void Fail(string agentKey, string runId, string userText, string failureCode)
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
                    new ChatEntry($"{runId}/assistant", ChatRole.Assistant, "")
                    {
                        Status = "failed",
                        Failure = failureCode,
                    },
                ]),
                Run = new ChatRunSnapshot(agentKey, 1, _revision, runId, ChatRunStatus.Failed),
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

    [Neuron("speechrecognizerneuron")]
    private sealed class FakeSpeechRecognizer : Neuron, IntoChat.OS.Modules.AI.Media.ISpeechRecognizer
    {
        public static bool AvailableResult { get; set; }

        public Task<IntoChat.OS.Modules.AI.Media.MediaCapabilities> Describe()
            => Task.FromResult(new IntoChat.OS.Modules.AI.Media.MediaCapabilities(
                IntoChat.OS.Modules.AI.Media.MediaOperation.SpeechRecognition,
                AvailableResult,
                1024));

        public Task<IntoChat.OS.Modules.AI.Media.SpeechRecognitionResult> Recognize(
            IntoChat.OS.Modules.AI.Media.SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Neuron("ai.models")]
    private sealed class FakeModelCatalog : Neuron, IModelCatalog
    {
        public static ModelId? PickedChat { get; set; }
        public static ModelCard[]? ListedCards { get; set; }
        public static ModelAvailability[]? ListedAvailability { get; set; }

        public static void Reset()
        {
            PickedChat = null;
            ListedCards = null;
            ListedAvailability = null;
        }

        public Task<ModelCard[]> List(Abilities need = Abilities.None)
        {
            if (ListedCards is { } cards)
            {
                return Task.FromResult(cards.Where(c => need == Abilities.None || (c.Can & need) == need).ToArray());
            }

            return Task.FromResult<ModelCard[]>(PickedChat is { } id && KnownModels.Find(id.Value) is { } card
                && (need == Abilities.None || (card.Can & need) == need)
                ? [card]
                : []);
        }

        public Task<ModelId> Pick(Role role, ModelId? turn = null)
            => turn is { Value.Length: > 0 } t ? Task.FromResult(t)
                : PickedChat is { Value.Length: > 0 } picked ? Task.FromResult(picked)
                : Task.FromResult(new ModelId(string.Empty));

        public Task SetRole(Role role, ModelId model)
            => Get<IUserSettings>(new NeuronId(Id.Brain, "os.settings", "settings")).SetRole(role, model);

        public Task<RoleMap> Roles() => Task.FromResult(KnownModels.Defaults);

        public Task<ModelAvailability[]> Availability() => Task.FromResult(ListedAvailability ?? []);

        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }

    [Neuron("devices.device")]
    private sealed class FakeDevice : Neuron, IDevice
    {
        public static string Machine { get; private set; } = "m1";
        public static List<ModelId> Installs { get; } = [];
        public static List<(Role Role, ModelId Model)> Uses { get; } = [];
        public static Install[] QueueEntries { get; set; } = [];
        public static DeviceModel[] DeviceModels { get; set; } = [];

        public static void Reset(string machineId)
        {
            Machine = string.IsNullOrEmpty(machineId) ? "m1" : machineId;
            Installs.Clear();
            Uses.Clear();
            QueueEntries = [];
            DeviceModels =
            [
                new DeviceModel(KnownModels.Qwen35, RuntimeId.Ollama, InstallState.Ready, Fit.Good),
                new DeviceModel(KnownModels.Gemma4, RuntimeId.Ollama, InstallState.Available, Fit.Good),
            ];
        }

        public Task<DeviceView> Read()
            => Task.FromResult(new DeviceView(
                Machine,
                new Hardware(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
                [new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434"))],
                DeviceModels,
                QueueEntries));

        public Task Use(Role role, ModelId model)
        {
            Uses.Add((role, model));
            return Task.CompletedTask;
        }

        public Task Install(ModelId model)
        {
            Installs.Add(model);
            var active = QueueEntries.FirstOrDefault(e => e.Model == model);
            return active is not null
                ? Publish(new InstallChanged(active.Model, active.State, active.Percent))
                : Task.CompletedTask;
        }

        public Task Cancel(ModelId model) => Task.CompletedTask;

        public Task Remove(ModelId model) => Task.CompletedTask;
    }
}
