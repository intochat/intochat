using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class ToggleSwitchNeuronFacts
{
    [Fact]
    public async Task SetChecked_sets_the_value_it_names_and_signals_Toggled()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron));

        await toggle.Send(new SetChecked(true));
        await toggle.Send(new SetChecked(false));

        Assert.Equal([new Toggled(true), new Toggled(false)], toggle.Published());
        Assert.False((await toggle.Drawn<ToggleSwitchView>()).IsOn);
    }

    [Fact]
    public async Task An_uncontrolled_SetChecked_signals_only_a_change()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron), state: new ToggleSwitchState { IsOn = true });

        await toggle.Send(new SetChecked(true));
        await toggle.Send(new SetChecked(false));

        Assert.Equal([new Toggled(false)], toggle.Published());
    }

    [Fact]
    public async Task It_draws_its_key_and_value()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron), key: "dark-mode",
            state: new ToggleSwitchState { IsOn = true });

        Assert.Equal(new ToggleSwitchView("dark-mode", true), await toggle.Drawn<ToggleSwitchView>());
    }
}
