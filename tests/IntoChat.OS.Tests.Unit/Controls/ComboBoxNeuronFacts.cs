using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class ComboBoxNeuronFacts
{
    [Fact]
    public async Task Select_changes_the_option_and_signals_SelectionChanged()
    {
        await using var combo = new ControlHarness(typeof(ComboBoxNeuron));

        await combo.Send(new Select("dark"));

        Assert.Equal([new SelectionChanged("dark")], combo.Published());
        Assert.Equal("dark", (await combo.Drawn<ComboBoxView>()).SelectedId);
    }

    [Fact]
    public async Task Selecting_the_current_option_publishes_nothing()
    {
        await using var combo = new ControlHarness(typeof(ComboBoxNeuron), state: new ComboBoxState { SelectedId = "light" });

        await combo.Send(new Select("light"));

        Assert.Empty(combo.Published());
    }

    [Fact]
    public async Task It_draws_its_key_and_selected_id()
    {
        await using var combo = new ControlHarness(typeof(ComboBoxNeuron), key: "theme", state: new ComboBoxState { SelectedId = "system" });

        Assert.Equal(new ComboBoxView("theme", "system"), await combo.Drawn<ComboBoxView>());
    }
}
