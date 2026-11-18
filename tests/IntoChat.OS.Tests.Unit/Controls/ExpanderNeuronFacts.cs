using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class ExpanderNeuronFacts
{
    [Fact]
    public async Task Expand_sets_and_signals_Expanded()
    {
        await using var expander = new ControlHarness(typeof(ExpanderNeuron));

        await expander.Send(new Expand(true));
        await expander.Send(new Expand(true));
        await expander.Send(new Expand(false));

        Assert.Equal([new Expanded(true), new Expanded(false)], expander.Published());
        Assert.False((await expander.Drawn<ExpanderView>()).IsExpanded);
    }

    [Fact]
    public async Task A_disabled_expander_refuses_Expand()
    {
        await using var expander = new ControlHarness(typeof(ExpanderNeuron), state: new ExpanderState { Header = "Locked", IsEnabled = false });

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => expander.Send(new Expand(true)));

        Assert.Equal("control.disabled", refused.Reason);
        Assert.Empty(expander.Published());
    }

    [Fact]
    public async Task It_draws_its_header_and_children_and_its_key_as_the_automation_name()
    {
        await using var expander = new ControlHarness(typeof(ExpanderNeuron), key: "advanced",
            state: new ExpanderState { Header = "Advanced", IsExpanded = true, Children = [new Text("Inside")] });

        Assert.Equal(new ExpanderView("advanced", "Advanced", true, true, "advanced", [new Text("Inside")]), await expander.Drawn<ExpanderView>());
    }
}
