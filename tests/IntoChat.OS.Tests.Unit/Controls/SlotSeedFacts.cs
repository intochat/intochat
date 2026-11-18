using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record SeedingParentState
{
    public string Label { get; init; } = "Terms";
}

/*
# Seeding parent

A test parent that slots a seeded check box and an unseeded one.
*/
[Neuron("test.seeding-parent")]
public sealed class SeedingParent : Screen<SeedingParentState>
{
    protected override View Render(SeedingParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Slot<CheckBoxNeuron, CheckBoxState>("terms", new CheckBoxState { Label = state.Label, IsOn = true }),
            Slot<CheckBoxNeuron>("plain"),
        ]));
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

// A parent's Slot(key, initial) seeds a child after the parent's save, only while the child has no stored state; the seed is accepted
// only from the parent; a child that fails to take its seed never fails the parent; an unseeded slot is still created on first use.
public sealed class SlotSeedFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.seeding-parent", "main");
    private static readonly NeuronId ChildId = new(Brain, "control.checkBox", ScreenExtensions.ChildAddress(ParentId, "terms"));
    private static readonly NeuronId PlainId = new(Brain, "control.checkBox", ScreenExtensions.ChildAddress(ParentId, "plain"));

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public SlotSeedFacts() => _os.Register<SeedingParent>();

    private IScreen Parent => _os.Get<IScreen>(ParentId);

    private async Task<CheckBoxView> Child()
    {
        var view = await _os.Get<IScreen>(ChildId).Read() ?? throw new InvalidOperationException("The child has no view.");
        return Assert.IsType<CheckBoxView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task The_seed_is_applied_when_the_slot_is_first_rendered()
    {
        _os.Register<CheckBoxNeuron>();
        Assert.NotNull(await Parent.Read());

        Assert.True(_os.IsActive(ChildId));
        var child = await Child();
        Assert.Equal(("Terms", true), (child.Label, child.IsOn));
    }

    [Fact]
    public async Task An_unseeded_slot_is_still_created_on_first_use()
    {
        _os.Register<CheckBoxNeuron>();
        await Parent.Read();

        Assert.False(_os.IsActive(PlainId));
    }

    [Fact]
    public async Task Once_the_child_has_state_the_seed_is_ignored()
    {
        _os.Register<CheckBoxNeuron>();
        _os.SeedStoredState(ChildId, JsonSerializer.Serialize(new CheckBoxState { Label = "Stored" }, JsonSerializerOptions.Web));
        await Parent.Read();

        var child = await Child();
        Assert.Equal(("Stored", false), (child.Label, child.IsOn));
    }

    [Fact]
    public async Task A_new_parent_view_does_not_reseed_a_child()
    {
        _os.Register<CheckBoxNeuron>();
        await Parent.Dispatch(new Command("terms", "control.toggle"));
        await Parent.Dispatch(new Command(".", "test.relabel", JsonSerializer.SerializeToElement(new Relabel("Renamed"), JsonSerializerOptions.Web)));

        var child = await Child();
        Assert.Equal(("Terms", false), (child.Label, child.IsOn));
    }

    [Fact]
    public async Task A_seed_from_another_caller_is_refused()
    {
        _os.Register<CheckBoxNeuron>();
        var seed = JsonSerializer.SerializeToElement(new CheckBoxState { Label = "Forged" }, JsonSerializerOptions.Web);

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => _os.Get<ISlotChild>(ChildId).SeedIfEmpty(seed));

        Assert.Equal(ActivationRefusedException.NotParent, refused.Reason);
        Assert.Equal("", (await Child()).Label);
    }

    [Fact]
    public async Task A_child_that_fails_to_take_its_seed_does_not_fail_the_parent()
    {
        // control.checkBox is not registered: seeding the child fails, and the parent's save and view stand.
        var view = await Parent.Read();

        Assert.NotNull(view);
        Assert.Contains(ChildId.ToString(), view.View.GetRawText(), StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
