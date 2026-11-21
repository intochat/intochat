using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Modules.UI.Chat.Signals;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record ThreadParentState
{
    public bool Working { get; init; }
}

[Signal("test.work")]
public sealed record Work(bool On) : Signal;

/*
# Thread parent

A test parent that slots a chat thread and writes it (an empty import) whenever it starts or stops working, so the thread draws its
stop from the props routed with that write.
*/
[Neuron("test.thread-parent")]
public sealed class ThreadParent : Screen<ThreadParentState>
{
    protected override View Render(ThreadParentState state)
        => new("Parent", Slot<ChatThreadNeuron, ChatThreadProps>("conversation", new ChatThreadProps(Working: state.Working, Status: state.Working ? "working" : "ready")));

    [On]
    public async Task Work(Work work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await Show(State with { Working = work.On });
        await Send(new Command("conversation", "control.importMessages", JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() })));
    }
}

public sealed class ChatThreadNeuronFacts
{
    private static ThreadMessage User(string id, string text) => new(id, "user", text);

    private static ThreadMessage Failed(string id, string? failure = null) => new(id, "assistant", "", "failed") { Failure = failure };

    [Fact]
    public async Task Import_is_idempotent_and_tolerates_null_or_duplicates()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron), key: "conversation");
        ThreadMessage?[] batch = [User("m1", "hello"), null, new ThreadMessage("", "user", "no id"), User("m2", "again"), User("m1", "a repeat of m1")];

        await thread.Send(new ImportMessages(batch));
        var first = await thread.Drawn<ChatThreadView>();
        await thread.Send(new ImportMessages(batch));
        await thread.Send(new ImportMessages(null));

        Assert.Equal(["m1", "m2"], first.Messages.Select(m => m.Id));
        Assert.Equal("hello", first.Messages[0].Text);
        Assert.Equal(first, await thread.Drawn<ChatThreadView>());
    }

    [Fact]
    public async Task Import_replaces_a_held_id_in_place_and_Replace_drops_the_rest()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));

        await thread.Send(new ImportMessages([User("m1", "one"), User("m2", "two")]));
        await thread.Send(new ImportMessages([User("m1", "one, edited")]));
        Assert.Equal([("m1", "one, edited"), ("m2", "two")], (await thread.Drawn<ChatThreadView>()).Messages.Select(m => (m.Id, m.Text)));

        await thread.Send(new ImportMessages([User("m3", "three")], Replace: true));
        Assert.Equal(["m3"], (await thread.Drawn<ChatThreadView>()).Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Append_and_Update_signal_and_ignore_what_they_cannot_apply()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));

        await thread.Send(new AppendMessage(User("m1", "hello")));
        await thread.Send(new AppendMessage(User("m1", "again")));
        await thread.Send(new UpdateMessage("m1", new ThreadMessagePatch(Text: "hello there")));
        await thread.Send(new UpdateMessage("nope", new ThreadMessagePatch(Text: "x")));

        Assert.Equal([new MessageAppended("m1"), new MessageUpdated("m1")], thread.Published());
        Assert.Equal("hello there", Assert.Single((await thread.Drawn<ChatThreadView>()).Messages).Text);
    }

    [Fact]
    public async Task Failed_reply_with_empty_failure_shows_reason_and_retry()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));

        await thread.Send(new ImportMessages([User("m1", "hi"), Failed("m2", ""), Failed("m3", null) with { Status = "interrupted" }]));

        var drawn = await thread.Drawn<ChatThreadView>();
        foreach (var message in drawn.Messages.Skip(1))
        {
            Assert.Equal(ChatThreadNeuron.NoAnswer, message.Error?.What);
            var retry = Assert.IsType<Button>(message.Retry);
            Assert.Equal(ChatThreadNeuron.RetryKeyOf(message.Id), retry.Key);
            Assert.Equal((".", "control.retry"), (retry.Command!.Target, retry.Command.Signal));
            Assert.Equal(message.Id, retry.Command.Args!.Value.GetProperty("id").GetString());
        }
        Assert.Null(drawn.Messages[0].Error);
        Assert.Null(drawn.Messages[0].Retry);
    }

    [Fact]
    public async Task No_failed_message_renders_blank()
    {
        string?[] reasons = [null, "", "  ", "The model provider did not answer."];
        var messages = Enumerable.Range(0, 60).SelectMany(i => new[]
        {
            User($"u{i}", $"question {i}"),
            Failed($"a{i}", reasons[i % reasons.Length]) with { Status = i % 3 == 0 ? "interrupted" : "failed" },
        }).ToArray();
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));

        await thread.Send(new ImportMessages(messages));

        var failed = (await thread.Drawn<ChatThreadView>()).Messages.Where(m => m.Role == "assistant").ToArray();
        Assert.Equal(50, failed.Length);
        Assert.All(failed, message =>
        {
            Assert.False(string.IsNullOrWhiteSpace(message.Error?.What));
            Assert.NotNull(message.Retry);
        });
    }

    [Fact]
    public async Task Retry_signals_the_message_id_and_refuses_an_unknown_one()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));
        await thread.Send(new ImportMessages([User("m1", "hi"), Failed("m2")]));

        await thread.Send(new Retry("m2"));
        await thread.Send(new Retry());
        await Assert.ThrowsAsync<ArgumentException>(() => thread.Send(new Retry("missing")));

        Assert.Equal([new RetryRequested("m2"), new RetryRequested("")], thread.Published());
    }

    [Fact]
    public async Task The_composer_submits_its_text_and_stop_asks_the_parent()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));

        await thread.Send(new SubmitText("hello"));
        await thread.Send(new SubmitText("   "));
        await thread.Send(new Stop());

        var published = thread.Published();
        Assert.Equal("hello", Assert.IsType<Submitted>(published[0]).Values["text"]);
        Assert.IsType<StopRequested>(Assert.Single(published.Skip(1)));
    }

    [Fact]
    public async Task The_composer_and_retry_nodes_are_activated_on_the_thread()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));
        await thread.Send(new ImportMessages([User("m1", "hi"), Failed("m2")]));
        var view = await thread.Screen.Read();

        await thread.Screen.Activate(view!.Alias, view.Generation, ChatThreadNeuron.ComposerKey, "typed text");
        await thread.Screen.Activate(view.Alias, view.Generation, ChatThreadNeuron.RetryKeyOf("m2"));

        var published = thread.Published();
        Assert.Equal("typed text", Assert.IsType<Submitted>(published[0]).Values["text"]);
        Assert.Equal(new RetryRequested("m2"), published[1]);
    }

    [Fact]
    public async Task The_stop_is_drawn_only_while_the_parent_says_it_is_working()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", ControlHarness.Brain));
        await using var os = DigitalBrain.Testing.TestOS.Local();
        os.Register<ThreadParent>().Register<ChatThreadNeuron>();
        var parent = new NeuronId(ControlHarness.Brain, "test.thread-parent", "p");
        var thread = os.Get<IScreen>(new NeuronId(ControlHarness.Brain, "control.chatThread", ScreenExtensions.ChildAddress(parent, "conversation")));
        async Task<ChatThreadView> Drawn() => Assert.IsType<ChatThreadView>((await thread.Read())!.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));

        await os.Get<IScreen>(parent).Request(new Work(false)); await os.Idle();
        Assert.Null((await Drawn()).Stop);

        await os.Get<IScreen>(parent).Request(new Work(true)); await os.Idle();
        Assert.Equal(ChatThreadNeuron.StopKey, (await Drawn()).Stop?.Key);

        await os.Get<IScreen>(parent).Request(new Work(false)); await os.Idle();
        Assert.Null((await Drawn()).Stop);
    }

    [Fact]
    public void The_props_wire_carries_no_slot_and_round_trips()
    {
        var props = new ChatThreadProps(
            "Ask anything.",
            CanSend: false,
            Status: "failed",
            Error: new ChatThreadError("Ino could not answer.", ActionLabel: "Try again", Retry: true),
            Models: [new ModelChoice("gpt-5.4", "GPT-5.4", "cloud", "provider:openai", ["tools", "vision"])],
            ActiveModelId: "gpt-5.4");
        var json = JsonSerializer.Serialize(props, JsonSerializerOptions.Web);
        Assert.Equal(props, JsonSerializer.Deserialize<ChatThreadProps>(json, JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task SelectModelPublishesChatModelSelected()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));

        await thread.Send(new SelectModel("claude-sonnet-5"));
        await thread.Send(new SelectModel("   "));

        var signal = Assert.IsType<ChatModelSelected>(Assert.Single(thread.Published()));
        Assert.Equal(("claude-sonnet-5", false), (signal.ModelId, signal.MakeDefault));
    }

    [Fact]
    public void TheThreadDrawsBothPickerNodes()
    {
        var view = ChatThreadNeuron.Draw("conversation", []);
        Assert.Equal(ChatThreadNeuron.SelectModelKey, view.SelectModel!.Key);
        Assert.Equal(ChatThreadNeuron.PinModelKey, view.PinModel!.Key);
        Assert.True(view.PinModel.OnSubmit.Args!.Value.GetProperty("default").GetBoolean());
    }

    [Fact]
    public async Task BothPickerNodesActivateOnTheThread()
    {
        await using var thread = new ControlHarness(typeof(ChatThreadNeuron));
        await thread.Send(new ImportMessages([]));
        var view = await thread.Screen.Read();

        await thread.Screen.Activate(view!.Alias, view.Generation, ChatThreadNeuron.SelectModelKey, "gpt-5.4");
        await thread.Screen.Activate(view.Alias, view.Generation, ChatThreadNeuron.PinModelKey, "claude-sonnet-5");

        var published = thread.Published();
        var switched = Assert.IsType<ChatModelSelected>(published[0]);
        Assert.Equal(("gpt-5.4", false), (switched.ModelId, switched.MakeDefault));
        var pinned = Assert.IsType<ChatModelSelected>(published[1]);
        Assert.Equal(("claude-sonnet-5", true), (pinned.ModelId, pinned.MakeDefault));
    }
}
