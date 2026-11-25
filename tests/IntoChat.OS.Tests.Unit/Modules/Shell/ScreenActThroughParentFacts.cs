using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Modules.Flutter.Screens;
using TestOS = DigitalBrain.Testing.TestOS;
using Xunit;

namespace IntoChat.OS.Modules.Flutter;

public sealed record DisabledSlotDemoState;

/*
# Disabled slot demo

A test app whose screen slots a chat thread under "conversation" with its props disabled, so a command to the thread must be refused
by the parent before the thread hears it.
*/
[Neuron("app.disabledslotdemo")]
public sealed class DisabledSlotDemoApp : Screen<DisabledSlotDemoState>
{
    protected override View Render(DisabledSlotDemoState state)
        => new("Disabled slot demo", new Column([new Text("Demo"), Child<ChatThreadNeuron, ChatThreadProps>("conversation", new ChatThreadProps("Ask anything.", IsEnabled: false))]));
}

// screen_act picks a node by key, takes its command and sends it through the top-level screen's Dispatch: a child's node is reached
// through its parent, which applies its gates first, and a generation the caller names is compared with the screen it read.
public sealed class ScreenActThroughParentFacts
{
    private static readonly BrainId Brain = new("owner");

    [Fact]
    public async Task A_child_node_is_acted_on_through_its_parent_with_the_acting_person()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);
        os.Register<SlotDemoApp>().Register<ChatThreadNeuron>();
        var app = new NeuronId(Brain, "app.slotdemo", "default");
        await os.Activate(app);
        var thread = new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(app, "conversation"));
        var directory = Directory(os);

        var acted = await directory.Act("slotdemo", "conversation/composer", "hello");
        await os.Idle();

        Assert.True(acted.Accepted, acted.Message);
        var submitted = Assert.Single(os.Journal(Brain).Signals(thread).Select(entry => entry.Signal).OfType<Submitted>());
        Assert.Equal("hello", submitted.Values["text"]);
    }

    [Fact]
    public async Task A_child_node_whose_parent_disables_it_is_refused_before_the_child_hears_it()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);
        os.Register<DisabledSlotDemoApp>().Register<ChatThreadNeuron>();
        var app = new NeuronId(Brain, "app.disabledslotdemo", "default");
        await os.Activate(app);
        var thread = new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(app, "conversation"));
        var directory = Directory(os);
        Assert.NotNull(await directory.Read("disabledslotdemo"));

        var acted = await directory.Act("disabledslotdemo", "conversation/composer", "hello");
        await os.Idle();

        Assert.False(acted.Accepted);
        Assert.Contains(ControlProps.Disabled, acted.Message, StringComparison.Ordinal);
        Assert.Empty(os.Journal(Brain).Signals(thread).Select(entry => entry.Signal).OfType<Submitted>());
    }

    [Fact]
    public async Task A_generation_the_caller_names_that_is_not_the_screens_is_refused_as_stale()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);
        os.Register<SlotDemoApp>().Register<ChatThreadNeuron>();
        var app = new NeuronId(Brain, "app.slotdemo", "default");
        await os.Activate(app);
        var thread = new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(app, "conversation"));
        var directory = Directory(os);
        var read = await directory.Read("slotdemo");
        Assert.NotNull(read);

        var stale = await directory.Act("slotdemo", "conversation/composer", "hello", generation: read.Generation + 1);
        var current = await directory.Act("slotdemo", "conversation/composer", "again", generation: read.Generation);
        await os.Idle();

        Assert.False(stale.Accepted);
        Assert.Contains(DigitalBrain.Units.ActivationRefusedException.Stale, stale.Message, StringComparison.Ordinal);
        Assert.True(current.Accepted, current.Message);
        var submitted = Assert.Single(os.Journal(Brain).Signals(thread).Select(entry => entry.Signal).OfType<Submitted>());
        Assert.Equal("again", submitted.Values["text"]);
    }

    [Fact]
    public async Task A_key_no_node_has_is_refused_as_an_unknown_node()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);
        os.Register<SlotDemoApp>().Register<ChatThreadNeuron>();
        await os.Activate(new NeuronId(Brain, "app.slotdemo", "default"));

        var acted = await Directory(os).Act("slotdemo", "conversation/nothing-here");

        Assert.False(acted.Accepted);
        Assert.Contains(DigitalBrain.Units.ActivationRefusedException.UnknownNode, acted.Message, StringComparison.Ordinal);
    }

    private static IScreenDirectory Directory(TestOS os)
        => os.Get<IScreenDirectory>(new NeuronId(Brain, IScreenDirectory.TypeName, IScreenDirectory.Key(Brain.Value)));
}
