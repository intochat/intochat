using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record SliderParentState;

/*
# Slider parent

A test parent that slots two sliders from 0 to 10: one that owns its value, one whose value the parent owns.
*/
[Neuron("test.slider-parent")]
public sealed class SliderParent : Screen<SliderParentState>
{
    protected override View Render(SliderParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<SliderNeuron, SliderProps>("free", new SliderProps("Depth", 0, 10)),
            Child<SliderNeuron, SliderProps>("owned", new SliderProps("Depth", 0, 10, Value: 4)),
        ]));
    }
}

public sealed class SliderNeuronFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.slider-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public SliderNeuronFacts()
    {
        _os.Register<SliderParent>();
        _os.Register<SliderNeuron>();
    }

    private Task Set(string slot, double value)
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(slot, SliderRefusals.SetValueSignal, JsonSerializer.SerializeToElement(new SetValue(value), JsonSerializerOptions.Web)));

    private NeuronId Slider(string slot) => new(Brain, "control.slider", ScreenExtensions.ChildAddress(ParentId, slot));

    private async Task<SliderView> Drawn(string slot)
    {
        var view = await _os.Get<IScreen>(Slider(slot)).Read() ?? throw new InvalidOperationException("no view");
        return Assert.IsType<SliderView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    private Signal[] Published(string slot) => [.. _os.Journal(Brain).Signals(Slider(slot)).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task Setting_a_value_stores_it_and_signals_ValueChanged_once()
    {
        await Set("free", 7.5);
        await Set("free", 7.5);

        Assert.Equal([new ValueChanged(7.5)], Published("free"));
        Assert.Equal(new SliderView("free", 7.5), await Drawn("free"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.5)]
    public async Task A_value_outside_the_props_range_is_refused_and_the_slider_is_not_reached(double value)
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Set("free", value));

        Assert.Equal(SliderRefusals.OutOfRange, refused.Reason);
        Assert.False(_os.IsActive(Slider("free")));
    }

    [Fact]
    public async Task A_set_without_a_number_is_refused()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => _os.Get<IScreen>(ParentId).Dispatch(new Command("free", SliderRefusals.SetValueSignal)));

        Assert.Equal(SliderRefusals.OutOfRange, refused.Reason);
    }

    [Fact]
    public async Task A_controlled_slider_always_signals_and_stores_nothing()
    {
        await Set("owned", 4);
        await Set("owned", 9);

        Assert.Equal([new ValueChanged(4), new ValueChanged(9)], Published("owned"));
        Assert.Null((await Drawn("owned")).Value);
    }

    [Fact]
    public void The_gate_names_the_set_value_command_alias()
        => Assert.Equal(SliderRefusals.SetValueSignal, typeof(SetValue).GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias);

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
