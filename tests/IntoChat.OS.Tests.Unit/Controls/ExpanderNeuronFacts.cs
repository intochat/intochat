using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
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
    public async Task It_draws_its_key_and_whether_it_is_expanded()
    {
        await using var expander = new ControlHarness(typeof(ExpanderNeuron), key: "advanced",
            state: new ExpanderState { IsExpanded = true });

        Assert.Equal(new ExpanderView("advanced", true), await expander.Drawn<ExpanderView>());
    }
}
