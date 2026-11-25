using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.Aspire;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoAppFacts
{
    private static readonly BrainId Brain = new("local");
    private static readonly Participant.Human Owner = new(new AccountId("local"));
    private static readonly Participant.Assistant InoAgent = new("ino");

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

    private static NeuronId ConversationOf(InoApp ino)
        => new(Brain, IConversation.Type, InoModel.ConversationKeyOf(ino.State.ConversationId));

    private static NeuronId SessionsId()
        => new(Brain, Sessions.Type, ISessionList.DefaultKey);

    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> InoOperations
        => ScreenBaseline.Cases(
            typeof(InoApp),
            os =>
            {
                os.RegisterCatalog();
            });

    [Theory, MemberData(nameof(InoOperations))]
    public Task InoBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task SendSaysOnTheConversationAndNeverStoresMessages()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend(Message: "hello"));

        var view = await os.Get<IConversation>(ConversationOf(ino.App)).Read();
        Assert.Equal("hello", view.Entries.OfType<Said>().First().Text);
        Assert.Null(ino.App.State.Messages);
    }

    [Fact]
    public async Task RetryOnAFailedMessageRegenerates()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("hello"));
        BackgroundConversation.Fail("main", "run-1", "hello", AgentFailure.ProviderUnavailable);
        await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

        var failedReplyId = EntryIds.Reply("run-1").Value;
        var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
        var view = await thread.Read();

        await os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default")).ClickChild(InoView.ConversationKey, ChatThreadProps.RetryKeyOf(failedReplyId));
        await os.Idle();

        Assert.Equal(1, BackgroundConversation.Regenerates);
        Assert.Equal(EntryIds.Reply("run-1"), BackgroundConversation.LastRegeneratedReply);

        BackgroundConversation.CompleteRegenerate("main", "run-2", EntryIds.Reply("run-1"), "hello", "Recovered answer.");
        await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

        var convView = await os.Get<IConversation>(ConversationOf(ino.App)).Read();
        var replacement = Assert.Single(convView.Entries.OfType<Said>(), static s => s.By is Participant.Assistant);
        Assert.Equal(EntryIds.Reply("run-1"), replacement.Replaces);
        Assert.Equal("Recovered answer.", replacement.Text);
        Assert.Contains("Recovered answer.", Texts(InoDrawing.Screen(ino.App)));
    }

    [Fact]
    public async Task NewConversationOpensASession()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("new", new InoNew());

        var current = await os.Get<ISessionList>(SessionsId()).Current();
        Assert.Equal(ino.App.State.ConversationId, current);
    }

    [Fact]
    public async Task ScenarioIsAnUnknownCommand()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("/scenario q4"));

        Assert.Contains("/scenario", ino.App.State.Status, StringComparison.Ordinal);
        Assert.Equal(0, BackgroundConversation.Says);
    }

    [Fact]
    public async Task ChatsOpTogglesTheSessionsListAndSwitchesConversation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var sessions = os.Get<ISessionList>(SessionsId());
        await sessions.Create("main", "Main chat");
        await sessions.Create("c-6c8edd77", "Bergen trip");
        var ino = await os.Install<InoApp>();

        await ino.Request("chats", new InoShowChats());
        Assert.Equal("chats", ino.App.State.ActiveSection);
        var rendered = new PresentedView("main", InoDrawing.Screen(ino.App));
        Assert.Contains(rendered.Entries, e => e.Title == "Bergen trip");
        Assert.Contains(rendered.Nodes.OfType<NavItemNode>(), n => n.Key == "session-c-6c8edd77");

        await ino.Request("switch-conversation", new InoSwitchConversation("c-6c8edd77"));
        Assert.Equal("c-6c8edd77", ino.App.State.ConversationId);
        Assert.Equal("chat", ino.App.State.ActiveSection);
        Assert.Equal("c-6c8edd77", await sessions.Current());
    }

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
        Assert.Contains("`engineer_runs_start_engineer_run`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`engineer_runs_inspect_engineer_run`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`studio_create_issue_triage_synapse`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`github_repository_dismiss_issue_draft`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`ai_memory_remember_preference`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`aspire_resources`", def.Instructions, StringComparison.Ordinal);
        Assert.Contains("`aspire_console_log`", def.Instructions, StringComparison.Ordinal);
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
        checkOs.RegisterCatalog();
        checkOs.Register(typeof(InoApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        checkOs.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        checkOs.Register(typeof(FlutterModule).Assembly);

        var desktopScreen = checkOs.Get<IScreen>(new NeuronId(Brain, "app.desktop", "default"));
        Assert.NotNull(await desktopScreen.Read());
        var galleryScreen = checkOs.Get<IScreen>(new NeuronId(Brain, "app.gallery", "default"));
        Assert.NotNull(await galleryScreen.Read());
        var photoCraftScreen = checkOs.Get<IScreen>(new NeuronId(Brain, "app.photocraft", "default"));
        Assert.NotNull(await photoCraftScreen.Read());
        var filmCraftScreen = checkOs.Get<IScreen>(new NeuronId(Brain, "app.filmcraft", "default"));
        Assert.NotNull(await filmCraftScreen.Read());
        var inoScreen = checkOs.Get<IScreen>(new NeuronId(Brain, "app.ino", "default"));
        Assert.NotNull(await inoScreen.Read());
        var friendsScreen = checkOs.Get<IScreen>(new NeuronId(Brain, "app.friends", "default"));
        Assert.NotNull(await friendsScreen.Read());

        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CreatorApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        var settings = os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings"));
        var updated = await settings.SetProfile("Vlad", "light", "gpt-5");
        Assert.Equal("Vlad", updated.DisplayName);
        Assert.Equal("light", updated.Theme);
        Assert.Equal("gpt-5", updated.DefaultModel);

        var profile = os.Get<IProfile>(new NeuronId(Brain, "os.profile", "profile"));
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
        Assert.Equal("/artcraft/photocraft/index.html", photoCraftInstalled.App.State.WebUrl);
        await photoCraftInstalled.Request("remove-background", new PhotoCraftRemoveBackground("/work/hero.png", "native-linux"));
        Assert.Contains("not connected", photoCraftInstalled.App.State.Status, StringComparison.Ordinal);
        Assert.Contains(photoCraftInstalled.Main.Texts, text => text.Contains("not connected", StringComparison.Ordinal));
        Assert.Empty(photoCraftInstalled.App.State.History);

        var filmCraftInstalled = await os.Install<FilmCraftApp>();
        Assert.Equal("/artcraft/filmcraft/index.html", filmCraftInstalled.App.State.WebUrl);
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

    private static TestOS ConversationOs()
    {
        var os = TestOS.Local(options => options.Services = WithRedactor());
        os.RegisterCatalog();
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        os.Register(typeof(FlutterModule).Assembly);
        return os;
    }

    [Fact]
    public async Task OneSendStartsExactlyOneConversationTurn()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("hello there"));
        await os.Idle();

        var view = await os.Get<IConversation>(ConversationOf(ino.App)).Read();
        var said = Assert.Single(view.Entries.OfType<Said>(), static e => e.By is Participant.Human);
        Assert.Equal("hello there", said.Text);
        if (ino.App.State.ActiveRunId is not null)
        {
            Assert.Equal(view.Active?.RunId, ino.App.State.ActiveRunId);
        }
        else
        {
            Assert.Single(view.Runs);
            Assert.Equal(InoModel.Failed, ino.App.State.Status);
            Assert.NotNull(ino.App.State.Error);
        }
    }

    [Fact]
    public async Task RuntimeModelSwitchViaSlashCommandSettingsAppAndUserSettingsPropagatesToConversationAndProfileContext()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        var settingsApp = await os.Install<SettingsApp>();
        var userSettings = os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings"));

        await ino.Request("send", new InoSend("turn 1"));
        Assert.Equal(KnownModels.Defaults.Device[ModelRole.Chat].Value, BackgroundConversation.LastSeatedModel);
        Assert.Equal(KnownModels.Defaults.Device[ModelRole.Chat].Value, BackgroundConversation.LastSayModel);
        BackgroundConversation.Complete("main", "run-1", "turn 1", "reply 1");
        await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

        await ino.Request("send", new InoSend("/model claude-sonnet-4-6"));
        Assert.Equal("Switched model to claude-sonnet-4-6", ino.App.State.Status);
        await os.Idle();
        Assert.Equal("claude-sonnet-4-6", ino.App.State.SelectedModel);
        Assert.Equal("claude-sonnet-4-6", (await userSettings.Read()).DefaultModel);
        Assert.Equal("claude-sonnet-4-6", BackgroundConversation.LastSeatedModel);

        await ino.Request("send", new InoSend("turn 2"));
        Assert.Equal("claude-sonnet-4-6", BackgroundConversation.LastSayModel);
        BackgroundConversation.Complete("main", "run-2", "turn 2", "reply 2");
        await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

        await settingsApp.Request("save-preferences", new SettingsSavePreferences(
            DisplayName: "Ada Lovelace",
            Theme: "dark",
            DefaultModel: "gpt-5.4",
            StylePreset: "fluent-acrylic",
            BarLayout: "floating-glass"));
        await os.Idle();
        Assert.Equal("gpt-5.4", (await userSettings.Read()).DefaultModel);
        Assert.Equal("gpt-5.4", BackgroundConversation.LastSeatedModel);

        await ino.Request("send", new InoSend("turn 3"));
        Assert.Equal("gpt-5.4", BackgroundConversation.LastSayModel);

        await using var convOs = ConversationOs();
        var liveIno = await convOs.Install<InoApp>();
        var liveProfile = convOs.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/user.profile"));
        await liveIno.Request("send", new InoSend("/model claude-sonnet-4-6"));
        await convOs.Idle();
        Assert.Equal("claude-sonnet-4-6", liveIno.App.State.SelectedModel);
        // ProfileContextWriter never writes an "inference" model entry into user.profile.
        Assert.DoesNotContain((await liveProfile.Read()).Entries, e => e.Name == "inference");
    }

    [Fact]
    public async Task DictationFollowsTheHearRole()
    {
        FakeModelCatalog.Reset();
        try
        {
            FakeModelCatalog.PickedRoles = new Dictionary<ModelRole, ModelId>
            {
                [ModelRole.Hear] = KnownModels.WhisperLargeV3Turbo.Id,
            };
            FakeModelCatalog.ListedAvailability =
            [
                new ModelAvailability(KnownModels.WhisperLargeV3Turbo.Id, Ready: true, Reason: null),
            ];

            await using var os = BackgroundOs();
            os.Register<FakeModelCatalog>();
            var ino = await os.Install<InoApp>();
            await ino.Request("open", new InoOpen());

            Assert.True(ino.App.Voice.Hear);
            Assert.True(InoDrawing.PropsOf(ino.App.Render()).Dictation);
            Assert.Null(typeof(InoAppState).GetProperty("Voice"));
            Assert.Null(typeof(InoAppState).GetProperty("CanDictate"));

            FakeModelCatalog.ListedAvailability =
            [
                new ModelAvailability(KnownModels.WhisperLargeV3Turbo.Id, Ready: false, Reason: "Not installed"),
            ];
            await ino.Request("open", new InoOpen());
            Assert.False(ino.App.Voice.Hear);
            Assert.False(InoDrawing.PropsOf(ino.App.Render()).Dictation);
        }
        finally
        {
            FakeModelCatalog.Reset();
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
            Assert.Equal("qwen3.5:4b", BackgroundConversation.LastSayModel);
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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();
        FakeModelCatalog.PickedChat = KnownModels.Qwen35.Id;

        try
        {
            var ino = await os.Install<InoApp>();
            await ino.Request("send", new InoSend("hello without ollama"));
            BackgroundConversation.Fail("main", "run-1", "hello without ollama", AgentFailure.ProviderUnavailable);
            await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

            Assert.Equal("no-provider", ino.App.State.Error?.Code);
            Assert.True(InoView.ShouldShowModelSetup(ino.App.State));
        }
        finally
        {
            FakeModelCatalog.Reset();
        }
    }

    [Fact]
    public async Task NewConversationMovesTheCurrentSessionBeforeTheNextSend()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("first"));

        await ino.Request("new", new InoNew());

        var current = await os.Get<ISessionList>(SessionsId()).Current();
        Assert.Equal(ino.App.State.ConversationId, current);
        Assert.Equal(1, BackgroundConversation.Says);
    }

    [Fact]
    public async Task SendStartsExactlyOnceAndReturnsBeforeCompletion()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("hello"));

        Assert.Equal(1, BackgroundConversation.Says);
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        Assert.Equal(InoModel.Working, ino.App.State.Status);
        Assert.Equal("main", await os.Get<ISessionList>(SessionsId()).Current());
    }

    [Fact]
    public async Task TurnCompletedUpdatesConversationAndRecordsCompute()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        BackgroundConversation.Complete("main", "run-1", "hello", "Hi from the background");

        await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

        Assert.Equal(InoModel.Ready, ino.App.State.Status);
        Assert.Null(ino.App.State.ActiveRunId);
        Assert.Contains("Hi from the background", Texts(InoDrawing.Screen(ino.App)));
        Assert.Equal(("ino", 1_000L), FakeCompute.Records.Single());
    }

    [Fact]
    public async Task A_raw_send_while_a_reply_runs_is_refused_and_starts_nothing()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Equal("run-1", ino.App.State.ActiveRunId);

        // Ino's own not_busy rule: the request reaches Ino while the run is active and is refused.
        await ino.Request("send", new InoSend("second question"));

        Assert.Equal(1, BackgroundConversation.Says);
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        Assert.NotEqual("second question", ino.App.State.Pending?.Text);
        Assert.DoesNotContain(InoView.Thread(ino.App.State, ino.App.CurrentView), m => m.Text == "second question");
    }

    [Fact]
    public async Task A_second_send_through_the_thread_while_a_reply_runs_starts_nothing()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
        var view = await thread.Read();

        // Ino said it cannot take a message: the thread refuses the text in order, before it could reach Ino.
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default")).ClickChild(InoView.ConversationKey, "composer", "second question"));
        await os.Idle();

        Assert.Equal(ControlProps.Disabled, refused.Reason);
        Assert.Equal(1, BackgroundConversation.Says);
        Assert.DoesNotContain(InoView.Thread(ino.App.State, ino.App.CurrentView), m => m.Text == "second question");
    }

    [Fact]
    public async Task ChoosingSwitchesOnlyThisConversation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();
        FakeModelCatalog.Reset();

        try
        {
            var ino = await os.Install<InoApp>();
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default")).ClickChild(InoView.ConversationKey, ChatThreadProps.SelectModelKey, "gpt-5.4");
            await os.Idle();

            Assert.Equal("gpt-5.4", ino.App.State.SelectedModel);
            var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
            Assert.Null(settings.RoleOf(ModelRole.Chat));
        }
        finally
        {
            FakeModelCatalog.Reset();
        }
    }

    [Fact]
    public async Task MakeDefaultPinsTheChatRole()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        os.Register<FakeModelCatalog>();
        FakeModelCatalog.Reset();

        try
        {
            var ino = await os.Install<InoApp>();
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default")).ClickChild(InoView.ConversationKey, ChatThreadProps.PinModelKey, "gpt-5.4");
            await os.Idle();

            Assert.Equal("gpt-5.4", ino.App.State.SelectedModel);
            var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
            Assert.Equal("gpt-5.4", settings.RoleOf(ModelRole.Chat)!.Value.Value);
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
        BackgroundConversation.Reset();
        FakeModelCatalog.Reset();
        FakeDevice.Reset(machine.Value);
        try
        {
            await using var os = TestOS.Local(o => o.Services = services);
            os.RegisterCatalog();
            os.Register<IntoChat.OS.Modules.Flutter.Conversations.SessionListNeuron>();
            os.Register<BackgroundConversation>();
            os.Register<FakeModelCatalog>();
            os.Register<FakeDevice>();

            var ino = await os.Install<InoApp>();
            var previousModel = ino.App.State.SelectedModel;
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default")).ClickChild(InoView.ConversationKey, ChatThreadProps.SelectModelKey, KnownModels.Gemma4.Id.Value);
            await os.Idle();

            Assert.Equal(KnownModels.Gemma4.Id, Assert.Single(FakeDevice.Installs));
            Assert.Empty(FakeDevice.Uses);
            Assert.Equal(previousModel, ino.App.State.SelectedModel);
        }
        finally
        {
            BackgroundConversation.Reset();
            FakeModelCatalog.Reset();
            FakeDevice.Reset(string.Empty);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task OnActivateReusesASingleCatalogAndDeviceSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-ino-snapshot-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor>(new NoSecrets())
            .AddSingleton(machine)
            .BuildServiceProvider();

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        BackgroundConversation.Reset();
        FakeModelCatalog.Reset();
        FakeDevice.Reset(machine.Value);
        try
        {
            FakeModelCatalog.PickedChat = KnownModels.Qwen35.Id;
            FakeModelCatalog.ListedCards = [KnownModels.Qwen35];
            FakeModelCatalog.ListedAvailability =
            [
                new ModelAvailability(KnownModels.Qwen35.Id, Ready: true, Reason: null),
            ];

            await using var os = TestOS.Local(o => o.Services = services);
            os.RegisterCatalog();
            os.Register<IntoChat.OS.Modules.Flutter.Conversations.SessionListNeuron>();
            os.Register<BackgroundConversation>();
            os.Register<FakeModelCatalog>();
            os.Register<FakeDevice>();

            var ino = await os.Install<InoApp>();

            Assert.Equal(KnownModels.Qwen35.Id.Value, ino.App.State.SelectedModel);
            Assert.Equal(1, FakeModelCatalog.ListCalls);
            Assert.Equal(1, FakeModelCatalog.AvailabilityCalls);
            Assert.Equal(1, FakeModelCatalog.PickChatCalls);
            Assert.Equal(1, FakeDevice.ReadCalls);
        }
        finally
        {
            BackgroundConversation.Reset();
            FakeModelCatalog.Reset();
            FakeDevice.Reset(string.Empty);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task OnActivateStaysWellUnderTimeoutWithAnUnreachableAttachedRuntime()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-ino-unreachable-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempDir, "machine-id"));
        var unreachableProbes = 0;
        using var slowUnreachableHandler = new SlowRefuseHandler(TimeSpan.FromMilliseconds(250), () => Interlocked.Increment(ref unreachableProbes));
        using var unreachableHttp = new HttpClient(slowUnreachableHandler, disposeHandler: false);
        var unreachableRuntime = new OpenAiCompatRuntime("lmstudio", new Uri("http://127.0.0.1:1234/v1"), unreachableHttp, TimeProvider.System);
        var localOllama = new ReadyOllamaStub(KnownModels.Qwen35);
        using var installer = new DeviceInstaller(IntoChat.OS.Tests.TestEdge.Authority);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
            })
            .Build();

        var sc = new ServiceCollection();
        sc.AddSingleton<IConfiguration>(config);
        sc.AddSingleton<ISecretRedactor>(new NoSecrets());
        sc.AddSingleton(TimeProvider.System);
        sc.AddSingleton(machine);
        sc.AddSingleton<IHardwareProbe>(new StubHardwareProbe());
        sc.AddSingleton(installer);
        sc.AddSingleton(IntoChat.OS.Tests.TestEdge.Authority);
        sc.AddSingleton<ILocalRuntime>(localOllama);
        sc.AddSingleton<ILocalRuntime>(unreachableRuntime);
        sc.AddOptions<AIOptions>();
        sc.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        sc.AddSingleton<IPriceList, ModelPriceList>();
        AIClients.Add(sc);
        await using var services = sc.BuildServiceProvider();

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        BackgroundConversation.Reset();
        try
        {
            await using var os = TestOS.Local(o => o.Services = services);
            os.RegisterCatalog();
            os.Register<IntoChat.OS.Modules.Flutter.Conversations.SessionListNeuron>();
            os.Register<BackgroundConversation>();
            os.Register<ModelCatalogNeuron>();
            os.Register<DeviceNeuron>();

            var started = Stopwatch.GetTimestamp();
            var ino = await os.Install<InoApp>();
            var elapsed = Stopwatch.GetElapsedTime(started);

            Assert.Equal(KnownModels.Qwen35.Id.Value, ino.App.State.SelectedModel);
            Assert.Equal(1, Volatile.Read(ref unreachableProbes));
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"InoApp.OnActivate took {elapsed.TotalSeconds:F2}s with an unreachable runtime.");
        }
        finally
        {
            BackgroundConversation.Reset();
            await installer.StopAsync(CancellationToken.None);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    private sealed class SlowRefuseHandler(TimeSpan delay, Action onCall) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onCall();
            await Task.Delay(delay, cancellationToken);
            throw new HttpRequestException("Connection refused");
        }
    }

    private sealed class StubHardwareProbe : IHardwareProbe
    {
        public Hardware Read() => new(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]);
        public void ReportNpu() { }
    }

    private sealed class ReadyOllamaStub(params ModelCard[] cards) : ILocalRuntime
    {
        public RuntimeId Id => RuntimeId.Ollama;

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, Reachable: true, Version: "0.9.0", Endpoint: new Uri("http://127.0.0.1:11434")));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in cards) { yield return card; }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in cards) { yield return card; }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Chat(ModelId id) => throw new NotSupportedException();
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
        BackgroundConversation.Reset();
        FakeModelCatalog.Reset();
        FakeDevice.Reset(machine.Value);
        try
        {
            await using var os = TestOS.Local(o => o.Services = services);
            os.RegisterCatalog();
            os.Register<IntoChat.OS.Modules.Flutter.Conversations.SessionListNeuron>();
            os.Register<BackgroundConversation>();
            os.Register<FakeModelCatalog>();
            os.Register<FakeDevice>();

            var ino = await os.Install<InoApp>();
            var thread = os.Get<IScreen>(new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.ino", "default"), InoView.ConversationKey)));
            var view = await thread.Read();

            await os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default")).ClickChild(InoView.ConversationKey, ChatThreadProps.PinModelKey, KnownModels.Gemma4.Id.Value);
            await os.Idle();

            Assert.Equal((ModelRole.Chat, KnownModels.Gemma4.Id), Assert.Single(FakeDevice.Uses));
            Assert.Empty(FakeDevice.Installs);
            var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
            Assert.Null(settings.RoleOf(ModelRole.Chat));
        }
        finally
        {
            BackgroundConversation.Reset();
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
        BackgroundConversation.Reset();
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
            os.RegisterCatalog();
            os.Register<IntoChat.OS.Modules.Flutter.Conversations.SessionListNeuron>();
            os.Register<BackgroundConversation>();
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
            BackgroundConversation.Reset();
            FakeModelCatalog.Reset();
            FakeDevice.Reset(string.Empty);
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

    [Fact]
    public async Task BlockedConversationPausesIno()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));

        BackgroundConversation.Block("main");
        await Deliver(ino, new ConversationChanged(ConversationId.Parse("main"), BackgroundConversation.Revision));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal(InoModel.Paused, ino.App.State.Status);
        Assert.Contains("Ino is paused.", Texts(InoDrawing.Screen(ino.App)));
    }

    [Fact]
    public async Task StopCancelsActiveRun()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("hello"));
        Assert.Contains("Stop", Texts(InoDrawing.Screen(ino.App)));

        await ino.Request("stop", new InoStop());

        Assert.Equal(1, BackgroundConversation.Stops);
    }

    [Fact]
    public async Task SmartToggleWritesVariable()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("smart", new InoSmart(true));

        var settings = await os.Get<IUserSettings>(new NeuronId(Brain, "os.settings", "settings")).Read();
        Assert.Contains(settings.Variables, v => v.Key == "ino.smart" && v.Value == "on");
        Assert.True(ino.App.State.Smart);
    }

    private static TestOS BackgroundOs()
    {
        BackgroundConversation.Reset();
        FakeCompute.Reset();
        var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register<IntoChat.OS.Modules.Flutter.Conversations.SessionListNeuron>();
        os.Register<BackgroundConversation>();
        os.Register<FakeCompute>();
        return os;
    }

    private static Task Deliver(InstalledApp<InoApp> ino, ConversationChanged signal)
        => ino.App.OnConversationChanged(signal);

    [Fact]
    public void AToolResultWithACardRendersACard()
    {
        var card = new InoCard("settings-models", "settings", "Settings · models / local-server", "Open settings", "neuron://local/apps/settings", "ready");
        var serialized = JsonSerializer.Serialize(new InoToolResult("Opened settings.", card), JsonSerializerOptions.Web);
        var at = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var convView = new ConversationView(
            ConversationId.Parse("main"),
            1,
            [Owner, InoAgent],
            [
                new Said(EntryIds.Said("r1"), Owner, at, "where is my ollama server?"),
                new Returned(EntryIds.Result("r1", "c1"), InoAgent, at, "os_settings_open", serialized, true, null),
                new Returned(EntryIds.Result("r1", "c2"), InoAgent, at, "os_settings_open", """{"text":"no card"}""", true, null),
                new Returned(EntryIds.Result("r1", "c3"), InoAgent, at, "os_settings_open", """{"error":"denied"}""", false, null),
                new Said(EntryIds.Reply("r1"), InoAgent, at, "Opened settings."),
            ],
            null,
            [new AgentRun("r1", AgentRunStatus.Completed, "where is my ollama server?", at, Reply: "Opened settings.")],
            false,
            []);

        var state = InoModel.FromConversation(new InoAppState(), convView);
        Assert.Equal(card, Assert.Single(state.ActiveCards));
        var view = InoDrawing.Screen(state, view: convView);
        Assert.Contains("Settings · models / local-server", Texts(view));
    }

    private static IReadOnlyList<string> Texts(View view)
        => new PresentedView("main", view).Texts;

    [Fact]
    public async Task NewRotatesTheConversationId()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("send", new InoSend("first"));
        var first = ino.App.State.ConversationId;

        await ino.Request("new", new InoNew());
        await ino.Request("send", new InoSend("second"));

        Assert.NotNull(first);
        Assert.NotEqual(first, ino.App.State.ConversationId);
        Assert.Equal($"conversations/{ino.App.State.ConversationId}", InoModel.ConversationKeyOf(ino.App.State.ConversationId));
    }

    [Fact]
    public async Task NewConversationMintsCanonicalConversationIdAndKey()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        var ino = await os.Install<InoApp>();
        await ino.Request("new", new InoNew());

        var convId = ino.App.State.ConversationId;
        Assert.NotNull(convId);
        Assert.StartsWith("c-", convId, StringComparison.Ordinal);
        Assert.Equal(34, convId.Length);
        Assert.True(ConversationId.IsCanonicalValue(convId));
        Assert.Equal($"conversations/{convId}", InoModel.ConversationKeyOf(convId));

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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        var ino = await os.Install<InoApp>();

        var initialTexts = Texts(InoDrawing.Screen(ino.App));
        Assert.All(initialTexts, text =>
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
    public async Task ABlockedConversationShowsPaused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        var ino = await os.Install<InoApp>();
        var focus = os.Get<IAgentContext>(new NeuronId(Brain, "agent-context", "context/ui.focus"));
        await Task.Run(async () =>
        {
            using var _ = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Kernel(Brain));
            await focus.Set(new("stop", "maintenance window", [], [ContextTags.Interrupt]));
        }, TestContext.Current.CancellationToken);
        await os.Idle();

        await ino.Request("send", new InoSend("hello"));

        Assert.True(ino.App.State.Blocked);
        Assert.Equal(InoModel.Paused, ino.App.State.Status);
        var c = InoDrawing.Conversation(ino.App.State);
        Assert.NotNull(c.Error);
        Assert.Equal("Ino is paused.", c.Error!.What);
        Assert.Contains("maintenance window", c.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenListsAgentGrants()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = ConversationOs();
        os.Register<GrantsNeuron>();
        var grants = os.Get<IGrantsNeuron>(new NeuronId(Brain, IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
        await grants.Grant(new Grant { AppId = CallerApps.Assistant, Scope = "run", Target = "local/ino/settings", WorkspaceId = "local" });
        await grants.Grant(new Grant { AppId = "other", Scope = "run", Target = "local/elsewhere", WorkspaceId = "local" });
        var ino = await os.Install<InoApp>();

        await ino.Request("open", new InoOpen());

        Assert.Contains("run · local/ino/settings", ino.App.State.GrantSummaries);
        Assert.DoesNotContain(ino.App.State.GrantSummaries, g => g.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OldTurnsAreArchivedNotReplayed()
    {
        var inoId = new NeuronId(Brain, "app.ino", "default");
        var legacyState = new InoAppState("main", "Legacy", "Ready", "idle", [new InoTurn("old question", "old reply", "agent", 0)], AgentKey: null);
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register<RecordingConversation>();
        os.SeedStoredState(inoId, JsonSerializer.Serialize(legacyState, RuntimeProtocol.OptionsFor(typeof(InoAppState))));
        RecordingConversation.Reset();

        var ino = await os.Install<InoApp>();
        var turns = ino.App.State.Turns;
        Assert.NotEmpty(turns);
        Assert.True(string.IsNullOrEmpty(ino.App.State.AgentKey));

        await ino.Request("open", new InoOpen());
        await ino.Request("open", new InoOpen());

        Assert.Equal(turns, ino.App.State.Turns);
        var post = Assert.Single(RecordingConversation.Posts);
        Assert.Equal(("Earlier conversation is in the archive.", "migration"), post);
        Assert.Equal(0, RecordingConversation.Says);
        Assert.False(string.IsNullOrEmpty(ino.App.State.AgentKey));
    }

    [Fact]
    public async Task AFailedMigrationPostIsRetriedOnTheNextOpen()
    {
        var inoId = new NeuronId(Brain, "app.ino", "default");
        var legacyState = new InoAppState("main", "Legacy", "Ready", "idle", [new InoTurn("old question", "old reply", "agent", 0)], AgentKey: null);
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register<RecordingConversation>();
        os.SeedStoredState(inoId, JsonSerializer.Serialize(legacyState, RuntimeProtocol.OptionsFor(typeof(InoAppState))));
        RecordingConversation.Reset();
        RecordingConversation.FailPosts = 1;

        try
        {
            var ino = await os.Install<InoApp>();
            await ino.Request("open", new InoOpen());
            Assert.True(string.IsNullOrEmpty(ino.App.State.AgentKey));

            await ino.Request("open", new InoOpen());

            Assert.Equal(("Earlier conversation is in the archive.", "migration"), Assert.Single(RecordingConversation.Posts));
            Assert.False(string.IsNullOrEmpty(ino.App.State.AgentKey));
        }
        finally { RecordingConversation.FailPosts = 0; }
    }

    [Fact]
    public async Task OnlyInoWritesItsLinks()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        var id = new NeuronId(Brain, "apps.ino-records", IInoRecords.Key);
        var journaled = os.Journal(Brain).Signals(id).Count();

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/desktop", new Actor.Person("p1", "a1"), Brain)))
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
        os.RegisterCatalog();
        os.Register<RecordingConversation>();
        RecordingConversation.Reset();
        var ino = await os.Install<InoApp>();
        var request = Guid.NewGuid();

        await ino.Request("link", new InoPasteLink(request, "notes", "page=3", "Notes, page 3"));
        await ino.Request("link", new InoPasteLink(request, "notes", "page=3", "Notes, page 3"));
        await os.Restart();

        var records = await os.Get<IInoRecords>(new NeuronId(Brain, "apps.ino-records", IInoRecords.Key)).Read();
        Assert.Equal(new InoLink(request.ToString("N"), "notes", "page=3"), Assert.Single(records.Links));
        var stored = JsonSerializer.Serialize(records.Links, JsonSerializerOptions.Web);
        Assert.Equal("page=3", InoLinkCard.FindLink(stored, request)?.Input);
        Assert.Contains(RecordingConversation.Posts, post => post == ("Notes, page 3", InoLinkCard.LinkPostId(request)));
    }

    [Fact]
    public async Task EmptySendIsIgnoredWithoutSendingHello()
    {
        await using var os = BackgroundOs();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend(null, null, null));
        await ino.Request("send", new InoSend("   "));

        Assert.Equal(0, BackgroundConversation.Says);
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
        os.RegisterCatalog();
        os.Register<RecordingConversation>();
        RecordingConversation.Reset();
        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("trigger start error"));

        var inoEntries = logs.Entries.Where(e => string.Equals(e.Category, typeof(InoApp).FullName, StringComparison.Ordinal)).ToArray();
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

        BackgroundConversation.Reset();
        DateTimeOffset? observedPendingAt = null;
        try
        {
            await using var os = TestOS.Local(o => o.Services = provider);
            os.RegisterCatalog();
            os.Register(typeof(FlutterModule).Assembly);
            os.Register<BackgroundConversation>();
            var ino = await os.Install<InoApp>();
            BackgroundConversation.OnSay = () => observedPendingAt = ino.App.State.Pending?.At;

            await ino.Request("send", new InoSend("check clock"));
            Assert.Equal(fixedNow, observedPendingAt);
        }
        finally
        {
            BackgroundConversation.Reset();
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
        var engineerWire = JsonSerializer.Serialize(engineerPreview);
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

        var navPayload = JsonSerializer.Serialize(
            new InoNavArgs(
                Prompt: "Triage GitHub issue #42",
                ConversationId: "conv-triage-42",
                FocusTarget: "node:app.tasks"),
            JsonSerializerOptions.Web);
        await screen.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
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

        await screen.Click(InoView.SendDraftKey);
        await os.Idle();
        Assert.Equal(1, BackgroundConversation.Says);
        Assert.Equal("run-1", ino.App.State.ActiveRunId);
        Assert.Null(ino.App.State.DraftPrompt);

        await screen.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.tasks", Route: "approvals", Force: false),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.Null(ino.App.LastRejection);

        await screen.Dispatch(new Command(
            ".",
            UiCommand.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiCommand("state", nameof(InoAppState.SetProviderSetupDraft), [JsonSerializer.SerializeToElement(true)]),
                typeof(UiCommand))));
        await os.Idle();
        Assert.True(ino.App.State.HasUnsavedProviderDraft);

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Dispatch(new Command(
                ".",
                UiNavigateFrom.SignalAlias,
                RuntimeProtocol.SerializeToElement(
                    new UiNavigateFrom(ToNeuron: "app.settings", Route: "connections/github", Force: false),
                    typeof(UiNavigateFrom)))));
        Assert.StartsWith("Rule:ino_setup_unsaved:", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Finish or discard provider credential setup before leaving.", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Finish or discard provider credential setup before leaving.", (await screen.Open())!.View.GetRawText(), StringComparison.Ordinal);

        await screen.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.settings", Route: "connections/github", Force: true),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.False(ino.App.State.HasUnsavedProviderDraft);
        Assert.Null(ino.App.LastRejection);

        await screen.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    FromNeuron: "app.engineer",
                    Transition: ViewTransition.FadeThrough,
                    Input: "Explain failing PR check"),
                typeof(UiNavigateTo))));
        await os.Idle();
        Assert.Equal("Explain failing PR check", ino.App.State.DraftPrompt);

        var seededAgain = (await screen.Open())!;
        await screen.Click(InoView.DismissDraftKey);
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

    [Neuron(IConversation.Type)]
    private sealed class RecordingConversation : Neuron, IConversation
    {
        public static List<(string Text, string Id)> Posts { get; } = [];
        public static int Says { get; set; }
        public static int FailPosts { get; set; }

        public static void Reset()
        {
            Posts.Clear();
            Says = 0;
            FailPosts = 0;
        }

        public Task Seat(string agentId, AgentDefinition definition) => Task.CompletedTask;
        public Task Say(string text, ModelId? model = null) { Says++; throw new NotSupportedException(); }
        public Task Stop() => Task.CompletedTask;
        public Task Regenerate(EntryId reply) => throw new NotSupportedException();

        public Task Post(string text, string id)
        {
            if (FailPosts > 0) { FailPosts--; throw new InvalidOperationException("Post failed."); }
            Posts.Add((text, id));
            return Task.CompletedTask;
        }

        public Task<ConversationView> Read(long? sinceRevision = null)
        {
            var rawId = Id.Key.StartsWith("conversations/", StringComparison.Ordinal) ? Id.Key["conversations/".Length..] : Id.Key;
            return Task.FromResult(new ConversationView(ConversationId.Parse(rawId), 0, [], [], null, [], false, []));
        }

        public Task Invite(AccountId friend) => throw new NotSupportedException();
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;
    }

    [Neuron(IConversation.Type)]
    private sealed class BackgroundConversation : Neuron, IConversation
    {
        public static int Says { get; set; }
        public static int Stops { get; set; }
        public static int Regenerates { get; set; }
        public static EntryId? LastRegeneratedReply { get; set; }
        public static string? LastSeatedModel { get; set; }
        public static string? LastSayModel { get; set; }
        public static Action? OnSay { get; set; }
        public static long Revision { get; private set; }
        private static readonly Dictionary<string, ConversationView> Views = new(StringComparer.Ordinal);
        private static AgentDefinition _definition = new();

        public static void Reset()
        {
            Says = 0;
            Stops = 0;
            Regenerates = 0;
            LastRegeneratedReply = null;
            LastSeatedModel = null;
            LastSayModel = null;
            OnSay = null;
            Revision = 0;
            Views.Clear();
            _definition = new();
        }

        private string ConvIdValue => Id.Key.StartsWith("conversations/", StringComparison.Ordinal) ? Id.Key["conversations/".Length..] : Id.Key;

        private static ConversationView GetOrEmpty(string convId)
            => Views.TryGetValue(convId, out var existing)
                ? existing
                : new ConversationView(ConversationId.Parse(convId), 0, [Owner, InoAgent], [], null, [], false, []);

        public static void Complete(string convId, string runId, string userText, string reply)
        {
            var now = DateTimeOffset.UtcNow;
            var current = GetOrEmpty(convId);
            Revision++;
            var entries = current.Entries.ToList();
            if (!entries.Any(e => e.Id == EntryIds.Said(runId)))
            {
                entries.Add(new Said(EntryIds.Said(runId), Owner, now, userText));
            }
            entries.Add(new Said(EntryIds.Reply(runId), InoAgent, now, reply));
            var runs = current.Runs.Where(r => r.RunId != runId).Append(new AgentRun(runId, AgentRunStatus.Completed, userText, now, Reply: reply)).ToArray();
            Views[convId] = current with { Revision = Revision, Entries = [.. entries], Active = null, Replacing = null, Runs = runs, Blocked = false };
        }

        public static void CompleteRegenerate(string convId, string runId, EntryId replaces, string userText, string reply)
        {
            var now = DateTimeOffset.UtcNow;
            var current = GetOrEmpty(convId);
            Revision++;
            var entries = current.Entries.Append(new Said(EntryIds.Reply(runId), InoAgent, now, reply, Replaces: replaces)).ToArray();
            var runs = current.Runs.Append(new AgentRun(runId, AgentRunStatus.Completed, userText, now, Reply: reply)).ToArray();
            Views[convId] = current with { Revision = Revision, Entries = entries, Active = null, Replacing = null, Runs = runs, Blocked = false };
        }

        public static void Fail(string convId, string runId, string userText, AgentFailure failure)
        {
            var now = DateTimeOffset.UtcNow;
            var current = GetOrEmpty(convId);
            Revision++;
            var entries = current.Entries.ToList();
            if (!entries.Any(e => e.Id == EntryIds.Said(runId)))
            {
                entries.Add(new Said(EntryIds.Said(runId), Owner, now, userText));
            }
            var runs = current.Runs.Where(r => r.RunId != runId).Append(new AgentRun(runId, AgentRunStatus.Failed, userText, now, Error: "failed") { Failure = failure }).ToArray();
            Views[convId] = current with { Revision = Revision, Entries = [.. entries], Active = null, Replacing = null, Runs = runs, Blocked = false };
        }

        public static void Block(string convId)
        {
            var current = GetOrEmpty(convId);
            Revision++;
            Views[convId] = current with { Revision = Revision, Active = null, Blocked = true };
        }

        public Task Seat(string agentId, AgentDefinition definition)
        {
            _definition = definition;
            LastSeatedModel = definition.Model?.Model;
            return Task.CompletedTask;
        }

        public Task Say(string text, ModelId? model = null)
        {
            OnSay?.Invoke();
            Says++;
            LastSayModel = model?.Value ?? _definition.Model?.Model;
            var convId = ConvIdValue;
            var runId = $"run-{Says}";
            var now = DateTimeOffset.UtcNow;
            var current = GetOrEmpty(convId);
            Revision++;
            var entries = current.Entries.Append(new Said(EntryIds.Said(runId), Owner, now, text)).ToArray();
            Views[convId] = current with
            {
                Revision = Revision,
                Entries = entries,
                Active = new AgentRun(runId, AgentRunStatus.Running, text, now),
                Replacing = null,
            };
            return Task.CompletedTask;
        }

        public Task Stop() { Stops++; return Task.CompletedTask; }

        public Task Regenerate(EntryId reply)
        {
            Regenerates++;
            LastRegeneratedReply = reply;
            var convId = ConvIdValue;
            var runId = $"regen-{Regenerates}";
            var now = DateTimeOffset.UtcNow;
            var current = GetOrEmpty(convId);
            Revision++;
            Views[convId] = current with
            {
                Revision = Revision,
                Active = new AgentRun(runId, AgentRunStatus.Running, current.Runs.LastOrDefault()?.UserText ?? "", now),
                Replacing = reply,
            };
            return Task.CompletedTask;
        }

        public Task Post(string text, string id) => Task.CompletedTask;

        public Task<ConversationView> Read(long? sinceRevision = null)
            => Task.FromResult(GetOrEmpty(ConvIdValue));

        public Task Invite(AccountId friend) => throw new NotSupportedException();
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;
    }

    [Neuron("ui.compute")]
    private sealed class FakeCompute : Neuron, ICompute
    {
        public static List<(string Key, long Micros)> Records { get; } = [];
        public static void Reset() => Records.Clear();
        public Task<ComputeState> Read() => Task.FromResult(new ComputeState(0, DateTimeOffset.UtcNow, []));
        public Task<ComputeState> SetBudget(long monthlyBudgetMicros) => Task.FromResult(new ComputeState(0, DateTimeOffset.UtcNow, [], monthlyBudgetMicros));
        public Task<ComputeState> Record(string key, long micros)
        {
            Records.Add((key, micros));
            return Read();
        }
    }

    [Neuron("ai.models")]
    private sealed class FakeModelCatalog : Neuron, IModelCatalog
    {
        public static ModelId? PickedChat { get; set; }
        public static Dictionary<ModelRole, ModelId>? PickedRoles { get; set; }
        public static ModelCard[]? ListedCards { get; set; }
        public static ModelAvailability[]? ListedAvailability { get; set; }
        public static int ListCalls { get; private set; }
        public static int AvailabilityCalls { get; private set; }
        public static int PickChatCalls { get; private set; }

        public static void Reset()
        {
            PickedChat = null;
            PickedRoles = null;
            ListedCards = null;
            ListedAvailability = null;
            ListCalls = 0;
            AvailabilityCalls = 0;
            PickChatCalls = 0;
        }

        public Task<ModelCard[]> List(Abilities need = Abilities.None)
        {
            ListCalls++;
            if (ListedCards is { } cards)
            {
                return Task.FromResult(cards.Where(c => need == Abilities.None || (c.Can & need) == need).ToArray());
            }

            return Task.FromResult<ModelCard[]>(PickedChat is { } id && KnownModels.Find(id.Value) is { } card
                && (need == Abilities.None || (card.Can & need) == need)
                ? [card]
                : []);
        }

        public Task<ModelId> Pick(ModelRole role, ModelId? turn = null)
        {
            if (role == ModelRole.Chat && turn is null)
            {
                PickChatCalls++;
            }

            if (turn is { Value.Length: > 0 } t)
            {
                return Task.FromResult(t);
            }

            if (PickedRoles is not null && PickedRoles.TryGetValue(role, out var byRole))
            {
                return Task.FromResult(byRole);
            }

            if (role == ModelRole.Chat && PickedChat is { Value.Length: > 0 } picked)
            {
                return Task.FromResult(picked);
            }

            return Task.FromResult(new ModelId(string.Empty));
        }

        public Task SetRole(ModelRole role, ModelId model)
            => Get<IUserSettings>(new NeuronId(Id.Brain, "os.settings", "settings")).SetRole(role, model);

        public Task<RoleMap> Roles() => Task.FromResult(KnownModels.Defaults);

        public Task<ModelAvailability[]> Availability()
        {
            AvailabilityCalls++;
            return Task.FromResult(ListedAvailability ?? []);
        }

        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }

    [Neuron("devices.device")]
    private sealed class FakeDevice : Neuron, IDevice
    {
        public static string Machine { get; private set; } = "m1";
        public static List<ModelId> Installs { get; } = [];
        public static List<(ModelRole Role, ModelId Model)> Uses { get; } = [];
        public static Install[] QueueEntries { get; set; } = [];
        public static DeviceModel[] DeviceModels { get; set; } = [];
        public static int ReadCalls { get; private set; }

        public static void Reset(string machineId)
        {
            Machine = string.IsNullOrEmpty(machineId) ? "m1" : machineId;
            Installs.Clear();
            Uses.Clear();
            QueueEntries = [];
            ReadCalls = 0;
            DeviceModels =
            [
                new DeviceModel(KnownModels.Qwen35, RuntimeId.Ollama, InstallState.Ready, Fit.Good),
                new DeviceModel(KnownModels.Gemma4, RuntimeId.Ollama, InstallState.Available, Fit.Good),
            ];
        }

        public Task<DeviceView> Read()
        {
            ReadCalls++;
            return Task.FromResult(new DeviceView(
                Machine,
                new Hardware(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
                [new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434"))],
                DeviceModels,
                QueueEntries));
        }

        public Task Use(ModelRole role, ModelId model)
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
