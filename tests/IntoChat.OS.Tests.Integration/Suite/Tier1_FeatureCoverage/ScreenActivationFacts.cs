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

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

public sealed class TestCounterState
{
    public int Count { get; init; }
    public string? LastBy { get; init; }
}

[Signal("test-counter.press")]
public sealed record TestCounterPress(
    string Key = "0",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("test-counter.noop")]
public sealed record TestCounterNoOp : Signal;

/*
# TestCounter
A test counter screen conforming to Screen contract conventions.
*/
[Neuron("app.test_counter")]
public sealed class TestCounterScreen : Screen<TestCounterState>
{
    protected override View Render(TestCounterState state)
    {
        return new View(
            "TestCounter",
            new Column([
                new Text($"Count: {state.Count}"),
                new Text($"LastBy: {state.LastBy ?? "none"}"),
                new Button("btn-press", "Press", Sends(new TestCounterPress("increment"))),
                new Field("fld-no-emit", "Input", FieldKind.Text),
                new Conversation("btn-alien-thread", [], new ComposerNode("btn-alien", new Emit("app.alien_owner", "test.alien", null))),
                new Text("static-text")
            ]));
    }

    [On]
    public Task OnPress(TestCounterPress signal)
    {
        var next = new TestCounterState
        {
            Count = State.Count + 1,
            LastBy = signal.By ?? Context.Caller?.ToString() ?? signal.Key
        };
        return Show(next);
    }

    [On]
    public Task OnNoOp(TestCounterNoOp signal)
    {
        _ = signal;
        return Show(State);
    }
}

public sealed class ScreenActivationFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public ScreenActivationFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T1_Screen_InitialActivation_GeneratesFirstPublishedViewAtGenerationOne()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-init");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal("app.test_counter", view.Alias);
        Assert.Equal(1L, view.Generation);

        var viewText = view.View.GetRawText();
        Assert.Contains("Count: 0", viewText);
    }

    [Fact]
    public async Task T1_Screen_Request_ExecutesReaction_IncrementsGenerationAndEmitsViewChanged()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-req");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Send Request to trigger reaction
        await screen.Request("test-counter.press", JsonSerializer.SerializeToElement(new TestCounterPress("inc")));
        await os.Idle();

        var updatedView = await screen.Read();
        Assert.NotNull(updatedView);
        Assert.Equal(2L, updatedView.Generation);
        Assert.Contains("Count: 1", updatedView.View.GetRawText());
    }

    [Fact]
    public async Task T1_Screen_IdenticalView_PreservesGeneration_WithoutViewChanged()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-noop");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        var view1 = await screen.Read();
        Assert.NotNull(view1);
        Assert.Equal(1L, view1.Generation);

        // Send NoOp signal that re-saves identical state
        await screen.Request("test-counter.noop", JsonSerializer.SerializeToElement(new TestCounterNoOp()));
        await os.Idle();

        var view2 = await screen.Read();
        Assert.NotNull(view2);
        // Generation must NOT increment for identical render
        Assert.Equal(1L, view2.Generation);
    }

    [Fact]
    public async Task T1_Screen_Activate_ValidInteraction_ExecutesReaction_WithHostAssertedBy()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-act");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);

        // Activate button with host-asserted caller "alice@intochat.io"
        await screen.Activate("app.test_counter", 1L, "btn-press", fieldValue: null, by: "alice@intochat.io");
        await os.Idle();

        var updatedView = await screen.Read();
        Assert.NotNull(updatedView);
        Assert.Equal(2L, updatedView.Generation);
        var text = updatedView.View.GetRawText();
        Assert.Contains("Count: 1", text);
        Assert.Contains("LastBy: alice@intochat.io", text);
    }

    [Fact]
    public async Task T1_Screen_Activate_StaleGeneration_ThrowsActivationRefusedException()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-stale");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        // Advance to generation 2
        await screen.Request("test-counter.press", JsonSerializer.SerializeToElement(new TestCounterPress("inc")));
        await os.Idle();

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(2L, view.Generation);

        // Attempt activation with stale generation 1L
        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.test_counter", 1L, "btn-press"));

        Assert.Equal(ActivationRefusedException.Stale, ex.Reason);
    }

    [Fact]
    public async Task T1_Screen_Activate_InvalidNodeOrOwner_ThrowsActivationRefusedException()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-invalids");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        // 1. Unknown node
        var exUnknown = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.test_counter", 1L, "ghost-key-does-not-exist"));
        Assert.Equal(ActivationRefusedException.UnknownNode, exUnknown.Reason);

        // 2. Node without emit (field with null OnSubmit)
        var exNoEmit = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.test_counter", 1L, "fld-no-emit"));
        Assert.Equal(ActivationRefusedException.NoEmit, exNoEmit.Reason);

        // 3. Emit with alien owner
        var exAlien = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.test_counter", 1L, "btn-alien"));
        Assert.Equal(ActivationRefusedException.ForeignOwner, exAlien.Reason);
    }

    [Fact]
    public async Task T1_Screen_Read_WithMismatchingAlias_ReturnsNull()
    {
        var os = _fixture.OS;
        os.Register<TestCounterScreen>();

        var brain = NewBrain("screen-alias");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.test_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        var matchView = await screen.Read("app.test_counter");
        Assert.NotNull(matchView);

        var mismatchView = await screen.Read("app.other_alias");
        Assert.Null(mismatchView);
    }
}
