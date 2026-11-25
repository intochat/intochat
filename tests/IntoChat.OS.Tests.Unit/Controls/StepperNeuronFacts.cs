using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;
using SelectCommand = DigitalBrain.UI.Commands.Select;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record StepperParentState;

/*
# Stepper parent

A test parent that slots two steppers over the same steps: one whose active step it leaves to the stepper, one whose active step it owns.
*/
[Neuron("test.stepper-parent")]
public sealed class StepperParent : Screen<StepperParentState>
{
    internal static readonly IReadOnlyList<StepperItem> Steps =
        [new("plan", "Plan", StepperStatus.Done), new("build", "Build", StepperStatus.Active), new("ship", "Ship", Description: "Release")];

    protected override View Render(StepperParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<StepperNeuron, StepperProps>("free", new StepperProps(Steps, "Release")),
            Child<StepperNeuron, StepperProps>("owned", new StepperProps(Steps, "Release", ActiveStepId: "build")),
        ]));
    }
}

public sealed class StepperNeuronFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.stepper-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public StepperNeuronFacts()
    {
        _os.Register<StepperParent>();
        _os.Register<StepperNeuron>();
    }

    private Task Select(string slot, string step)
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(slot, SelectionRefusals.SelectSignal, JsonSerializer.SerializeToElement(new SelectCommand(step), JsonSerializerOptions.Web)));

    private NeuronId Stepper(string slot) => new(Brain, "control.stepper", ScreenExtensions.ChildAddress(ParentId, slot));

    private async Task<StepperView> Drawn(string slot)
    {
        var view = await _os.Get<IScreen>(Stepper(slot)).Read() ?? throw new InvalidOperationException("no view");
        return Assert.IsType<StepperView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    private Signal[] Published(string slot) => [.. _os.Journal(Brain).Signals(Stepper(slot)).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task Selecting_a_step_stores_it_and_signals_SelectionChanged_once()
    {
        await Select("free", "ship");
        await Select("free", "ship");

        Assert.Equal([new SelectionChanged("ship")], Published("free"));
        Assert.Equal(new StepperView("free", "ship"), await Drawn("free"));
    }

    [Fact]
    public async Task A_step_the_props_do_not_hold_is_refused_and_the_stepper_is_not_reached()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Select("free", "deploy"));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
        Assert.False(_os.IsActive(Stepper("free")));
    }

    [Fact]
    public async Task A_controlled_stepper_always_signals_and_stores_nothing()
    {
        await Select("owned", "ship");
        await Select("owned", "build");

        Assert.Equal([new SelectionChanged("ship"), new SelectionChanged("build")], Published("owned"));
        Assert.Null((await Drawn("owned")).ActiveStepId);
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
