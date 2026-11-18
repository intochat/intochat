using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class CheckBoxNeuronFacts
{
    [Fact]
    public async Task Toggle_flips_and_signals_Toggled()
    {
        await using var box = new ControlHarness(typeof(CheckBoxNeuron));

        await box.Send(new Toggle());
        await box.Send(new Toggle());

        Assert.Equal([new Toggled(true), new Toggled(false)], box.Published());
        Assert.False((await box.Drawn<CheckBoxView>()).IsOn);
    }

    [Fact]
    public async Task SetChecked_signals_only_a_change()
    {
        await using var box = new ControlHarness(typeof(CheckBoxNeuron));

        await box.Send(new SetChecked(true));
        await box.Send(new SetChecked(true));

        Assert.Equal([new Toggled(true)], box.Published());
        Assert.True((await box.Drawn<CheckBoxView>()).IsOn);
    }

    [Fact]
    public async Task A_disabled_box_refuses_Toggle_and_SetChecked()
    {
        await using var box = new ControlHarness(typeof(CheckBoxNeuron), state: new CheckBoxState { Label = "Locked", IsEnabled = false });

        var toggle = await Assert.ThrowsAsync<ActivationRefusedException>(() => box.Send(new Toggle()));
        var set = await Assert.ThrowsAsync<ActivationRefusedException>(() => box.Send(new SetChecked(true)));

        Assert.Equal(ControlRefusals.Disabled, toggle.Reason);
        Assert.Equal(ControlRefusals.Disabled, set.Reason);
        Assert.Empty(box.Published());
        Assert.False((await box.Drawn<CheckBoxView>()).IsOn);
    }

    [Fact]
    public async Task It_draws_its_state_and_its_key_as_the_automation_name()
    {
        await using var box = new ControlHarness(typeof(CheckBoxNeuron), key: "terms", state: new CheckBoxState { Label = "I accept", IsThreeState = true });

        var drawn = await box.Drawn<CheckBoxView>();

        Assert.Equal(new CheckBoxView("terms", "I accept", false, true, "terms", IsThreeState: true), drawn);
    }

    [Fact]
    public async Task An_automation_name_overrides_the_key()
    {
        await using var box = new ControlHarness(typeof(CheckBoxNeuron), state: new CheckBoxState { Label = "x", AutomationName = "Accept the terms" });

        Assert.Equal("Accept the terms", (await box.Drawn<CheckBoxView>()).AutomationName);
    }
}
