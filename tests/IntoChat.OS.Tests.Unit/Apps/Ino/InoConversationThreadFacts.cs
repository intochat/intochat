using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

// Ino's messages live in its conversation aggregate and draw through its chatThread slot, never in Ino's stored state.
public sealed class InoConversationThreadFacts
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId InoId = new(Brain, "app.ino", "default");
    private static readonly NeuronId ThreadId = new(Brain, "control.chatThread", ScreenExtensions.ChildAddress(InoId, InoView.ConversationKey));
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Human Owner = new(new AccountId("local"));
    private static readonly Participant.Assistant Ino = new("ino");

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private static TestOS CreateOs(InoAppState? state = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor, NoSecrets>()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = services);
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.Flutter.FlutterModule).Assembly);
        os.Register<InoApp>();
        if (state is not null)
        {
            os.SeedStoredState(InoId, JsonSerializer.Serialize(state, RuntimeProtocol.OptionsFor(typeof(InoAppState))));
        }
        return os;
    }

    private static async Task<ChatThreadView> ThreadOf(TestOS os)
    {
        var view = await os.Get<IScreen>(ThreadId).Read() ?? throw new InvalidOperationException("The thread has no view.");
        return Assert.IsType<ChatThreadView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task Ino_loads_messages_from_the_conversation_and_never_stores_them_in_state()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var conv = os.Get<IConversation>(new NeuronId(Brain, IConversation.Type, InoModel.ConversationKeyOf("main")));
        await conv.Post("Welcome back.", "welcome");

        var ino = await os.Install<InoApp>();

        Assert.Null(ino.App.State.Messages);
        var thread = await ThreadOf(os);
        var msg = Assert.Single(thread.Messages);
        Assert.Equal("Welcome back.", msg.Text);

        await ino.Request("open", new InoOpen());
        Assert.Null(ino.App.State.Messages);
    }

    [Fact]
    public async Task Legacy_stored_state_with_messages_still_loads_and_clears_messages_on_save()
    {
        const string preThreadState = """
            {"conversationId":"main","title":"first question","status":"Ready","voiceStatus":"idle","turns":[],"cards":[],"connectedDatabase":null,
             "isPinned":true,"agentKey":"agent-main","activeRunId":null,"blocked":false,"grantSummaries":null,"blockedReason":null,"seenProgress":null,
             "messages":[{"id":"r1/user","role":"user","text":"first question","status":"completed"}],
             "chatRevision":7,"pending":null,"error":null,"approvals":null,"smart":false,"selectedModel":null,"detectedLocalModel":null,"detectedLocalModels":null}
            """;
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor, NoSecrets>()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var os = TestOS.Local(options => options.Services = services);
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.Flutter.FlutterModule).Assembly);
        os.Register<InoApp>();
        os.SeedStoredState(InoId, preThreadState);

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var ino = await os.Install<InoApp>();

        Assert.Equal("main", ino.App.State.ConversationId);
        Assert.Equal("first question", ino.App.State.Title);
        Assert.Null(ino.App.State.Messages);
    }

    [Fact]
    public async Task The_composer_sends_through_the_thread_to_the_conversation()
    {
        await using var os = CreateOs(new InoAppState());
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var ino = await os.Install<InoApp>();
        var thread = os.Get<IScreen>(ThreadId);
        var view = await thread.Read();

        await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.ComposerKey, "hello there");
        await os.Idle();

        var conv = os.Get<IConversation>(new NeuronId(Brain, IConversation.Type, InoModel.ConversationKeyOf(ino.App.State.ConversationId)));
        var said = Assert.Single((await conv.Read()).Entries.OfType<Said>(), static e => e.By is Participant.Human);
        Assert.Equal("hello there", said.Text);
    }

    [Fact]
    public async Task A_thread_that_cannot_be_written_is_said_in_the_slot_props()
    {
        // No chat thread neuron is registered: every write to the thread fails.
        await using var os = CreateOs(new InoAppState());
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        await os.Activate(InoId);
        var ino = os.Get<IScreen>(InoId);

        await ino.Request(new InoSend("hello"));
        await os.Idle();

        var view = (await ino.Read())!.View.Deserialize<View>(RuntimeProtocol.OptionsFor(typeof(View)))!;
        Assert.Equal(InoApp.ThreadFailure, InoDrawing.PropsOf(view).Error?.What);
    }

    [Fact]
    public void Every_failed_reply_says_why_in_Ino_words()
    {
        var runs = new[]
        {
            new AgentRun("r1", AgentRunStatus.Failed, "a", At) { Failure = AgentFailure.Unknown },
            new AgentRun("r2", AgentRunStatus.Cancelled, "b", At, Reply: "partial"),
            new AgentRun("r3", AgentRunStatus.Interrupted, "c", At, Reply: "partial"),
            new AgentRun("r4", AgentRunStatus.Failed, "d", At) { Failure = AgentFailure.ProviderUnavailable },
        };
        ConversationEntry[] entries =
        [
            new Said(EntryIds.Said("r1"), Owner, At, "a"),
            new Said(EntryIds.Said("r2"), Owner, At, "b"),
            new Said(EntryIds.Said("r3"), Owner, At, "c"),
            new Said(EntryIds.Said("r4"), Owner, At, "d"),
        ];
        var view = new ConversationView(ConversationId.Parse("main"), 1, [Owner, Ino], entries, null, runs, false, []);
        var thread = InoView.Thread(new InoAppState(), view);

        var failures = thread.Where(m => m.Role == "assistant").Select(m => m.Failure).ToArray();

        Assert.Equal(["Ino could not answer.", "Stopped.", "Ino was interrupted.", "The model provider did not answer."], failures);
        Assert.All(ChatThreadNeuron.Draw("conversation", thread).Messages.Where(m => m.Role == "assistant"),
            m => Assert.NotNull(m.Retry));
    }
}
