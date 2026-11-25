using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class TabStripNeuronFacts
{
    [Fact]
    public async Task Select_changes_the_tab_and_signals_SelectionChanged()
    {
        await using var tabs = new ControlHarness(typeof(TabStripNeuron));

        await tabs.Send(new Select("privacy"));

        Assert.Equal([new SelectionChanged("privacy")], tabs.Published());
        Assert.Equal("privacy", (await tabs.Drawn<TabStripView>()).SelectedId);
    }

    [Fact]
    public async Task Selecting_the_current_tab_publishes_nothing()
    {
        await using var tabs = new ControlHarness(typeof(TabStripNeuron), state: new TabStripState { SelectedId = "general" });

        await tabs.Send(new Select("general"));

        Assert.Empty(tabs.Published());
    }

    [Fact]
    public async Task It_draws_its_key_and_selected_id()
    {
        await using var tabs = new ControlHarness(typeof(TabStripNeuron), key: "sections", state: new TabStripState { SelectedId = "about" });

        Assert.Equal(new TabStripView("sections", "about"), await tabs.Drawn<TabStripView>());
    }
}
