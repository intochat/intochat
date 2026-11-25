using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests;

public sealed record GuardProbeState
{
    public int Pings { get; init; }
    public string Refusal { get; init; } = "";
}

[Signal("guard-probe.ping")]
public sealed record GuardProbePing : Signal;

[Signal("guard-probe.meddle")]
public sealed record GuardProbeMeddle(string Child) : Signal;

// A parent with one child that counts pings.
[Neuron("app.guard-probe")]
public sealed class GuardProbeScreen : Screen<GuardProbeState>
{
    protected override View Render(GuardProbeState state) => new("Guard probe", new Column([Child<GuardProbeChildScreen>("inner")]));
}

[Neuron("test.guard-probe-child")]
public sealed class GuardProbeChildScreen : Screen<GuardProbeState>
{
    protected override View Render(GuardProbeState state) => new("Guard probe child", new Text($"pings={state.Pings}"));

    [On]
    public Task When(GuardProbePing _) => Show(State with { Pings = State.Pings + 1 });
}

// A neuron of the same brain that is not the child's parent: it sends the ping straight to the child and keeps the refusal.
[Neuron("app.guard-probe-meddler")]
public sealed class GuardProbeMeddlerScreen : Screen<GuardProbeState>
{
    protected override View Render(GuardProbeState state) => new("Meddler", new Text($"refusal={state.Refusal}"));

    [On]
    public async Task When(GuardProbeMeddle signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        try
        {
            await Get<IScreen>(NeuronId.Parse(signal.Child)).Dispatch(new Command(".", "guard-probe.ping"));
            await Show(State with { Refusal = "none" });
        }
        catch (ActivationRefusedException refusal)
        {
            await Show(State with { Refusal = refusal.Reason });
        }
    }
}

[Module("guard-probe")]
public sealed class GuardProbeModule : DigitalBrain.Modules.Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class GuardProbeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [typeof(GuardProbeModule)];
}

// C2: a screen's child takes commands only from its parent, on TestOS and on a silo. A command through the parent reaches it; the same
// command sent straight to the child, by a person or by another neuron of the brain, is refused with control.notFromParent.
public sealed class ChildCommandsFromParentFacts(GuardProbeFixture fixture) : IClassFixture<GuardProbeFixture>
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task A_command_through_the_parent_reaches_the_child(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("alice-principal", "alice-account", runtime.Brain));
        var (parent, child) = await Probe(runtime, "through");

        await parent.Dispatch(new Command("inner", "guard-probe.ping"));
        await runtime.Idle();

        Assert.Contains("pings=1", (await runtime.Get<IScreen>(child).Read())!.View.GetRawText(), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task A_command_a_person_sends_straight_to_the_child_is_refused(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("alice-principal", "alice-account", runtime.Brain));
        var (_, child) = await Probe(runtime, "person");

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => runtime.Get<IScreen>(child).Dispatch(new Command(".", "guard-probe.ping")));
        await runtime.Idle();

        Assert.Contains(ControlProps.NotFromParent, refused.ToString(), StringComparison.Ordinal);
        Assert.Contains("pings=0", (await runtime.Get<IScreen>(child).Read())!.View.GetRawText(), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task A_command_another_neuron_sends_straight_to_the_child_is_refused(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("alice-principal", "alice-account", runtime.Brain));
        var (_, child) = await Probe(runtime, "meddler");
        var meddler = runtime.Get<IScreen>(runtime.Id("app.guard-probe-meddler", "meddler"));

        await meddler.Dispatch(new Command(".", "guard-probe.meddle", JsonSerializer.SerializeToElement(new GuardProbeMeddle(child.ToString()), JsonSerializerOptions.Web)));
        await runtime.Idle();

        Assert.Contains($"refusal={ControlProps.NotFromParent}", (await meddler.Read())!.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("pings=0", (await runtime.Get<IScreen>(child).Read())!.View.GetRawText(), StringComparison.Ordinal);
    }

    // A parent of its own per fact (the class fixture's runtime is shared), read once so its child is routed.
    private static async Task<(IScreen Parent, NeuronId Child)> Probe(INeuronRuntime runtime, string key)
    {
        var parentId = runtime.Id("app.guard-probe", key);
        var parent = runtime.Get<IScreen>(parentId);
        _ = await parent.Read();
        return (parent, runtime.Id("test.guard-probe-child", ScreenExtensions.ChildAddress(parentId, "inner")));
    }
}
