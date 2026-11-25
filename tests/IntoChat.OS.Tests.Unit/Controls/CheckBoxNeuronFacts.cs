using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Modules.UI.Controls;

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
    public async Task It_draws_its_key_and_value()
    {
        await using var box = new ControlHarness(typeof(CheckBoxNeuron), key: "terms", state: new CheckBoxState { IsOn = true });

        var drawn = await box.Drawn<CheckBoxView>();

        Assert.Equal(new CheckBoxView("terms", true), drawn);
    }
}
