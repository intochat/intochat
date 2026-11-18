using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record SeedingParentState
{
    public string Label { get; init; } = "Terms";
}

/*
# Seeding parent

A test parent that slots one check box with a starting state.
*/
[Neuron("test.seeding-parent")]
public sealed class SeedingParent : Screen<SeedingParentState>
{
    protected override View Render(SeedingParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([Slot<CheckBoxNeuron, CheckBoxState>("terms", new CheckBoxState { Label = state.Label, IsOn = true })]));
    }

    [On]
    public Task Relabel(Relabel signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Show(State with { Label = signal.Label });
    }
}

[Signal("test.relabel")]
public sealed record Relabel(string Label) : Signal;

// A parent's Slot(key, initial) seeds a child only while the child has no stored state; rendering the slot creates nothing.
public sealed class SlotSeedFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.seeding-parent", "main");
    private static readonly NeuronId ChildId = new(Brain, "control.checkBox", ScreenExtensions.ChildAddress(ParentId, "terms"));

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public SlotSeedFacts()
    {
        _os.Register<SeedingParent>();
        _os.Register<CheckBoxNeuron>();
    }

    private IScreen Parent => _os.Get<IScreen>(ParentId);

    private async Task<CheckBoxView> Child()
    {
        var view = await _os.Get<IScreen>(ChildId).Read() ?? throw new InvalidOperationException("The child has no view.");
        return Assert.IsType<CheckBoxView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task Rendering_a_seeded_slot_creates_nothing()
    {
        Assert.NotNull(await Parent.Read());

        Assert.False(_os.IsActive(ChildId));
    }

    [Fact]
    public async Task The_seed_is_the_first_state_of_a_child_read_by_the_client()
    {
        await Parent.Read();

        var child = await Child();

        Assert.Equal(("Terms", true), (child.Label, child.IsOn));
    }

    [Fact]
    public async Task The_seed_is_the_first_state_of_a_child_reached_through_the_parent()
    {
        await Parent.Dispatch(new Command("terms", "control.toggle"));

        var child = await Child();

        Assert.Equal(("Terms", false), (child.Label, child.IsOn));
    }

    [Fact]
    public async Task Once_the_child_has_state_the_seed_is_ignored()
    {
        _os.SeedStoredState(ChildId, JsonSerializer.Serialize(new CheckBoxState { Label = "Stored" }, JsonSerializerOptions.Web));
        await Parent.Read();

        Assert.Equal(("Stored", false), ((await Child()).Label, (await Child()).IsOn));
    }

    [Fact]
    public async Task A_new_seed_does_not_overwrite_a_created_child()
    {
        await Parent.Dispatch(new Command("terms", "control.toggle"));
        await Parent.Dispatch(new Command(".", "test.relabel", JsonSerializer.SerializeToElement(new Relabel("Renamed"), JsonSerializerOptions.Web)));
        await _os.Restart();

        var child = await Child();

        Assert.Equal(("Terms", false), (child.Label, child.IsOn));
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
