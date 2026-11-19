using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Modules.Flutter.Screens;
using TestOS = DigitalBrain.Testing.TestOS;
using Xunit;

namespace IntoChat.OS.Modules.Flutter;

public sealed record SlotDemoState;

/*
# Slot demo

A test app whose screen slots a chat thread under "conversation", so the screen directory must read and act through the slot.
*/
[Neuron("app.slotdemo")]
public sealed class SlotDemoApp : Screen<SlotDemoState>
{
    protected override View Render(SlotDemoState state)
        => new("Slot demo", new Column([new Text("Demo"), Slot<ChatThreadNeuron, ChatThreadProps>("conversation", new ChatThreadProps("Ask anything.", Status: "failed", Error: new ChatThreadError("Ino could not answer.", ActionLabel: "Try again", Retry: true)))]));
}

// MCP and HTTP readers see a screen's slots in place, their controls under the slot key path, and act on them through the slot.
public sealed class ScreenDirectorySlotFacts
{
    private static readonly BrainId Brain = new("owner");

    [Fact]
    public async Task A_slot_child_is_read_in_place_and_acted_on_by_its_key_path()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Brain));
        await using var os = TestOS.Local().Register(typeof(FlutterModule).Assembly);
        os.Register<SlotDemoApp>().Register<ChatThreadNeuron>();
        var app = new NeuronId(Brain, "app.slotdemo", "default");
        await os.Activate(app);
        var thread = new NeuronId(Brain, "control.chatThread", ScreenExtensions.ChildAddress(app, "conversation"));
        var directory = os.Get<IScreenDirectory>(new NeuronId(Brain, IScreenDirectory.TypeName, IScreenDirectory.Key(Brain.Value)));

        var read = await directory.Read("slotdemo");

        Assert.NotNull(read);
        Assert.Contains(read.Controls, control => control.Key == "conversation/composer");
        var drawn = Assert.IsType<DigitalBrain.UI.Controls.ChatThreadView>(Assert.IsType<Column>(read.View!.Root).Children[1]);
        // The parent's props stay readable in place: the status and the error no message carries.
        Assert.Equal(("failed", "Ino could not answer."), (drawn.Props?.Status, drawn.Props?.Error?.What));
        // Idle: the thread was never told it is working, so no stop is offered.
        Assert.DoesNotContain(read.Controls, control => control.Key == "conversation/stop");

        var acted = await directory.Act("slotdemo", "conversation/composer", "hello");
        await os.Idle();

        Assert.True(acted.Accepted, acted.Message);
        var submitted = Assert.Single(os.Journal(Brain).Signals(thread).Select(entry => entry.Signal).OfType<Submitted>());
        Assert.Equal("hello", submitted.Values["text"]);
    }
}
