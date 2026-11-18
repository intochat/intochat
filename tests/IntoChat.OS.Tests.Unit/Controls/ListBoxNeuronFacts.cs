using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class ListBoxNeuronFacts
{
    [Fact]
    public async Task Select_changes_the_item_and_signals_SelectionChanged()
    {
        await using var list = new ControlHarness(typeof(ListBoxNeuron));

        await list.Send(new Select("phone"));

        Assert.Equal([new SelectionChanged("phone")], list.Published());
        Assert.Equal("phone", (await list.Drawn<ListBoxView>()).SelectedId);
    }

    [Fact]
    public async Task Selecting_the_current_item_publishes_nothing()
    {
        await using var list = new ControlHarness(typeof(ListBoxNeuron), state: new ListBoxState { SelectedId = "laptop" });

        await list.Send(new Select("laptop"));

        Assert.Empty(list.Published());
    }

    [Fact]
    public async Task It_draws_its_key_and_selected_id()
    {
        await using var list = new ControlHarness(typeof(ListBoxNeuron), key: "devices", state: new ListBoxState { SelectedId = "tablet" });

        Assert.Equal(new ListBoxView("devices", "tablet"), await list.Drawn<ListBoxView>());
    }
}
