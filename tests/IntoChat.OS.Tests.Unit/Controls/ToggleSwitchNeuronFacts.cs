using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class ToggleSwitchNeuronFacts
{
    [Fact]
    public async Task Toggle_flips_and_signals_Toggled()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron));

        await toggle.Send(new Toggle());
        await toggle.Send(new Toggle());

        Assert.Equal([new Toggled(true), new Toggled(false)], toggle.Published());
        Assert.False((await toggle.Drawn<ToggleSwitchView>()).IsOn);
    }

    [Fact]
    public async Task SetChecked_signals_only_a_change()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron), state: new ToggleSwitchState { IsOn = true });

        await toggle.Send(new SetChecked(true));
        await toggle.Send(new SetChecked(false));

        Assert.Equal([new Toggled(false)], toggle.Published());
    }

    [Fact]
    public async Task It_draws_its_state_and_its_key_as_the_automation_name()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron), key: "dark-mode",
            state: new ToggleSwitchState { Label = "Dark mode", IsOn = true, OnContent = "On", OffContent = "Off" });

        Assert.Equal(new ToggleSwitchView("dark-mode", "Dark mode", true, true, "dark-mode", "On", "Off"), await toggle.Drawn<ToggleSwitchView>());
    }

    [Fact]
    public async Task A_disabled_switch_refuses_Toggle_and_SetChecked()
    {
        await using var toggle = new ControlHarness(typeof(ToggleSwitchNeuron), state: new ToggleSwitchState { IsEnabled = false });

        var flip = await Assert.ThrowsAsync<ActivationRefusedException>(() => toggle.Send(new Toggle()));
        var set = await Assert.ThrowsAsync<ActivationRefusedException>(() => toggle.Send(new SetChecked(true)));

        Assert.Equal("control.disabled", flip.Reason);
        Assert.Equal("control.disabled", set.Reason);
        Assert.Empty(toggle.Published());
        Assert.False((await toggle.Drawn<ToggleSwitchView>()).IsEnabled);
    }
}
