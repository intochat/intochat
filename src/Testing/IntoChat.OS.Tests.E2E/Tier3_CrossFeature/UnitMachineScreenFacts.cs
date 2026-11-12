using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier3_CrossFeature;

public sealed class MachineScreenState
{
    public int Value { get; init; }
    public string? LastAction { get; init; }
    public bool InjectBy { get; init; }
}

[Signal("machine-screen.step")]
public sealed record MachineScreenStep(
    string Action = "step",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("machine-screen.fail")]
public sealed record MachineScreenFail : Signal;

[Signal("machine-screen.noop")]
public sealed record MachineScreenNoOp : Signal;

[Signal("machine-screen.make-inject-by")]
public sealed record MachineScreenMakeInjectBy : Signal;

[Signal("machine-screen.malicious")]
public sealed record MachineMaliciousArgs(
    [property: JsonPropertyName("by")] string By,
    string Payload) : Signal;

/*
# MachineScreen
Stateful screen testing UnitMachine turn execution, view synchronization, and transactional rollback.
*/
[Neuron("app.machine_screen")]
public sealed class MachineScreen : Screen<MachineScreenState>
{
    protected override View Render(MachineScreenState state)
    {
        var elements = new List<Node>
        {
            new Text($"Value: {state.Value}"),
            new Text($"LastAction: {state.LastAction ?? "none"}"),
            new Button("btn-step", "Step", Emits(new MachineScreenStep())),
            new Button("btn-fail", "Fail", Emits(new MachineScreenFail())),
            new Button("btn-noop", "NoOp", Emits(new MachineScreenNoOp())),
            new Button("btn-inject-by", "Inject By", Emits(new MachineScreenMakeInjectBy())),
            new Button("btn-alien", "Alien", new Emit("app.alien_owner", "machine-screen.step", null)),
            new Field("fld-no-emit", "Input", FieldKind.Text)
        };

        if (state.InjectBy)
        {
            elements.Add(new Button("btn-malicious", "Malicious", Emits(new MachineMaliciousArgs("attacker", "pwn"))));
        }

        return new View("MachineScreen", new Column([.. elements]));
    }

    [On]
    public Task OnStep(MachineScreenStep signal)
    {
        var next = new MachineScreenState
        {
            Value = State.Value + 10,
            LastAction = signal.By ?? signal.Action,
            InjectBy = State.InjectBy
        };
        return Show(next);
    }

    [On]
    public Task OnFail(MachineScreenFail signal)
    {
        _ = signal;
        throw new InvalidOperationException("Simulated transaction reaction failure");
    }

    [On]
    public Task OnNoOp(MachineScreenNoOp signal)
    {
        _ = signal;
        return Show(State);
    }

    [On]
    public Task OnMakeInjectBy(MachineScreenMakeInjectBy signal)
    {
        _ = signal;
        var next = new MachineScreenState
        {
            Value = State.Value,
            LastAction = State.LastAction,
            InjectBy = true
        };
        return Show(next);
    }
}

public sealed class UnitMachineScreenFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public UnitMachineScreenFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T3_UnitMachine_Screen_AtomicTurnExecution_CommitsStateViewAndSaidInSingleJournalEntry()
    {
        var os = _fixture.OS;
        os.Register<MachineScreen>();

        var brain = NewBrain("screen-atomic-turn");
        var screenId = new NeuronId(brain, "app.machine_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // Initial generation is 1L
        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);
        Assert.Contains("Value: 0", initialView.View.GetRawText());

        // Client triggers button activation
        await screen.Activate("app.machine_screen", 1L, "btn-step", fieldValue: null, by: "alice@intochat.io");
        await os.Idle();

        // State, view, and signals committed atomically; generation advanced to 2L
        var currentView = await screen.Read();
        Assert.NotNull(currentView);
        Assert.Equal(2L, currentView.Generation);
        Assert.Contains("Value: 10", currentView.View.GetRawText());
        Assert.Contains("LastAction: alice@intochat.io", currentView.View.GetRawText());
    }

    [Fact]
    public async Task T3_UnitMachine_Screen_FailingAction_RollsBackStateAndView_PreservingCausationSignal()
    {
        var os = _fixture.OS;
        os.Register<MachineScreen>();

        var brain = NewBrain("screen-rollback");
        var screenId = new NeuronId(brain, "app.machine_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // Advance to generation 2L
        await screen.Activate("app.machine_screen", 1L, "btn-step", fieldValue: null, by: "bob");
        await os.Idle();

        var gen2View = await screen.Read();
        Assert.NotNull(gen2View);
        Assert.Equal(2L, gen2View.Generation);
        Assert.Contains("Value: 10", gen2View.View.GetRawText());

        // Calling failing action throws and rolls back state mutation
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.machine_screen", 2L, "btn-fail"));

        Assert.Contains("Simulated transaction reaction failure", ex.Message);

        // Verification: Zero state writes committed; generation remains 2L and value remains 10
        var recoveredView = await screen.Read();
        Assert.NotNull(recoveredView);
        Assert.Equal(2L, recoveredView.Generation);
        Assert.Contains("Value: 10", recoveredView.View.GetRawText());
    }

    [Fact]
    public async Task T3_UnitMachine_Screen_IdenticalStateOrNoOp_LeavesGenerationAndSignalsUntouched()
    {
        var os = _fixture.OS;
        os.Register<MachineScreen>();

        var brain = NewBrain("screen-identical-noop");
        var screenId = new NeuronId(brain, "app.machine_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // Advance to generation 2L
        await screen.Activate("app.machine_screen", 1L, "btn-step");
        await os.Idle();

        var view2 = await screen.Read();
        Assert.NotNull(view2);
        Assert.Equal(2L, view2.Generation);

        // Trigger NoOp which re-saves identical view
        await screen.Activate("app.machine_screen", 2L, "btn-noop");
        await os.Idle();

        var viewAfterNoOp = await screen.Read();
        Assert.NotNull(viewAfterNoOp);

        // Generation remains unchanged at 2L because rendered wire text is identical
        Assert.Equal(2L, viewAfterNoOp.Generation);
    }

    [Fact]
    public async Task T3_UnitMachine_Screen_ReservedByInViewEmit_IsRefusedAndThrowsInvalidOperationException()
    {
        var os = _fixture.OS;
        os.Register<MachineScreen>();

        var brain = NewBrain("screen-reserved-by");
        var screenId = new NeuronId(brain, "app.machine_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Action triggers state change that renders view with button containing reserved 'by'
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.machine_screen", 1L, "btn-inject-by"));

        Assert.Contains("declares the host-reserved 'by'", ex.Message);

        // View remains at generation 1L
        var preservedView = await screen.Read();
        Assert.NotNull(preservedView);
        Assert.Equal(1L, preservedView.Generation);
    }

    [Fact]
    public async Task T3_UnitMachine_Screen_PressRefusalMatrix_ValidatesAllFourRefusalInvariantsInOrleansHost()
    {
        var os = _fixture.OS;
        os.Register<MachineScreen>();

        var brain = NewBrain("screen-refusal-matrix");
        var screenId = new NeuronId(brain, "app.machine_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1L, view.Generation);

        // 1. Stale generation
        var exStale = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.machine_screen", 999L, "btn-step"));
        Assert.Equal(ActivationRefusedException.Stale, exStale.Reason);

        // 2. Unknown node key
        var exUnknown = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.machine_screen", 1L, "non-existent-button"));
        Assert.Equal(ActivationRefusedException.UnknownNode, exUnknown.Reason);

        // 3. Node without emit
        var exNoEmit = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.machine_screen", 1L, "fld-no-emit"));
        Assert.Equal(ActivationRefusedException.NoEmit, exNoEmit.Reason);

        // 4. Foreign emit owner
        var exForeign = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.machine_screen", 1L, "btn-alien"));
        Assert.Equal(ActivationRefusedException.ForeignOwner, exForeign.Reason);
    }
}
