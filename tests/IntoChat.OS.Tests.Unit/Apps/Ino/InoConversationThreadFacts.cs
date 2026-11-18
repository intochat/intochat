using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

// Ino's messages live in its conversation thread (a control.chatThread slot under "conversation"), never in Ino's stored state.
public sealed class InoConversationThreadFacts
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId InoId = new(Brain, "app.ino", "default");
    private static readonly NeuronId ThreadId = new(Brain, "control.chatThread", ScreenExtensions.ChildAddress(InoId, InoView.ConversationKey));

    private static readonly InoMessage[] Legacy =
    [
        new("r1/user", "user", "first question"),
        new("r1/assistant", "assistant", "", "failed") { Failure = "timed-out" },
        new("r2/user", "user", "second question"),
        new("r2/assistant", "assistant", "An answer."),
    ];

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class OfflineLocalProbe : ILocalOllamaProbe
    {
        public Task<LocalOllamaDetection?> Detect(CancellationToken ct = default) => Task.FromResult<LocalOllamaDetection?>(null);
    }

    private static TestOS SeededOs(InoAppState state)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor, NoSecrets>()
            .AddSingleton<ILocalOllamaProbe, OfflineLocalProbe>()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = services);
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);
        os.Register<InoApp>();
        os.SeedStoredState(InoId, JsonSerializer.Serialize(state, RuntimeProtocol.OptionsFor(typeof(InoAppState))));
        return os;
    }

    private static async Task<ChatThreadView> ThreadOf(TestOS os)
    {
        var view = await os.Get<IScreen>(ThreadId).Read() ?? throw new InvalidOperationException("The thread has no view.");
        return Assert.IsType<ChatThreadView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task Ino_migrates_once_and_clears_state()
    {
        await using var os = SeededOs(new InoAppState() with { Messages = Legacy });

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var ino = await os.Install<InoApp>();

        Assert.Null(ino.App.State.Messages);
        var migrated = await ThreadOf(os);
        Assert.Equal(Legacy.Select(m => m.Id), migrated.Messages.Select(m => m.Id));
        Assert.Equal("The model provider did not answer.", migrated.Messages[1].Error?.What);

        await ino.Request("open", new InoOpen());
        await ino.Request("open", new InoOpen());

        Assert.Null(ino.App.State.Messages);
        Assert.Equal(Legacy.Select(m => m.Id), (await ThreadOf(os)).Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Retry_on_older_message_resends_that_turn()
    {
        await using var os = SeededOs(new InoAppState() with { Messages = Legacy });
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var ino = await os.Install<InoApp>();
        var thread = os.Get<IScreen>(ThreadId);
        var view = await thread.Read();

        await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.RetryKeyOf("r1/assistant"));
        await os.Idle();

        var agent = new NeuronId(Brain, AgentSignals.NeuronType, InoModel.AgentKeyOf(ino.App.State.ConversationId));
        var started = Assert.Single(os.Journal(Brain).Signals(agent).Select(entry => entry.Signal).OfType<TurnStarted>());
        Assert.Equal("first question", started.UserText);
    }

    [Fact]
    public async Task The_composer_sends_through_the_thread()
    {
        await using var os = SeededOs(new InoAppState());
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var ino = await os.Install<InoApp>();
        var thread = os.Get<IScreen>(ThreadId);
        var view = await thread.Read();

        await thread.Activate(view!.Alias, view.Generation, ChatThreadNeuron.ComposerKey, "hello there");
        await os.Idle();

        var agent = new NeuronId(Brain, AgentSignals.NeuronType, InoModel.AgentKeyOf(ino.App.State.ConversationId));
        Assert.Equal("hello there", Assert.Single(os.Journal(Brain).Signals(agent).Select(entry => entry.Signal).OfType<TurnStarted>()).UserText);
    }

    [Fact]
    public void Every_failed_reply_says_why_in_Ino_words()
    {
        var s = new InoAppState() with
        {
            Messages =
            [
                new("r1/user", "user", "a"),
                new("r1/assistant", "assistant", "", "failed"),
                new("r2/user", "user", "b"),
                new("r2/assistant", "assistant", "", "failed") { Failure = "" },
                new("r3/user", "user", "c"),
                new("r3/assistant", "assistant", "partial", "interrupted"),
                new("r4/user", "user", "d"),
                new("r4/assistant", "assistant", "", "failed") { Failure = "provider-unavailable" },
            ],
        };

        var failures = InoView.Thread(s).Where(m => m.Role == "assistant").Select(m => m.Failure).ToArray();

        Assert.Equal([ChatThreadNeuron.NoAnswer, ChatThreadNeuron.NoAnswer, "Ino was interrupted.", "The model provider did not answer."], failures);
        Assert.All(ChatThreadNeuron.Draw("conversation", InoView.Thread(s)).Messages.Where(m => m.Role == "assistant"),
            m => Assert.NotNull(m.Retry));
    }
}
