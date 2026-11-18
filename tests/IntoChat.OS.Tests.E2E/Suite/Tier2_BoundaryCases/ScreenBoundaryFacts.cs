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

namespace IntoChat.OS.Tests.E2E.Tier2_BoundaryCases;

public sealed class BoundaryTestState
{
    public int Count { get; init; }
    public string? LastBy { get; init; }
    public bool Oversized { get; init; }
    public bool InjectBy { get; init; }
}

[Signal("boundary-test.inc")]
public sealed record BoundaryInc(
    string Action = "inc",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("boundary-test.make-oversized")]
public sealed record BoundaryMakeOversized : Signal;

[Signal("boundary-test.make-inject-by")]
public sealed record BoundaryMakeInjectBy : Signal;

[Signal("boundary-test.noop")]
public sealed record BoundaryNoOp : Signal;

[Signal("boundary-test.malicious")]
public sealed record MaliciousByArgs(
    [property: JsonPropertyName("by")] string By,
    string Payload) : Signal;

/*
# BoundaryTest
Screen for testing boundary and corner conditions in Screen<TState>.
*/
[Neuron("app.boundary_screen")]
public sealed class BoundaryTestScreen : Screen<BoundaryTestState>
{
    protected override View Render(BoundaryTestState state)
    {
        var elements = new List<Node>
        {
            new Text($"Count: {state.Count}"),
            new Text($"LastBy: {state.LastBy ?? "none"}"),
            new Button("btn-inc", "Increment", Sends(new BoundaryInc("inc"))),
            new Switch("btn-alien", "Alien Button", false, new Emit("app.foreign_owner", "boundary-test.inc", null)),
            new Button("btn-oversized", "Make Oversized", Sends(new BoundaryMakeOversized())),
            new Button("btn-inject-by", "Make Inject By", Sends(new BoundaryMakeInjectBy())),
            new Field("fld-no-emit", "Input", FieldKind.Text),
            new Text("txt-static")
        };

        if (state.Oversized)
        {
            // Inject text payload exceeding 2,097,152 bytes
            elements.Add(new Text(new string('X', 2_097_153)));
        }

        if (state.InjectBy)
        {
            // Inject author-declared host-reserved 'by' in args via typed record
            elements.Add(new Button("btn-malicious", "Malicious", Sends(new MaliciousByArgs("hacker", "exploit"))));
        }

        return new View("BoundaryTest", new Column([.. elements]));
    }

    [On]
    public Task OnInc(BoundaryInc signal)
    {
        var next = new BoundaryTestState
        {
            Count = State.Count + 1,
            LastBy = signal.By ?? Context.Caller?.ToString() ?? signal.Action,
            Oversized = State.Oversized,
            InjectBy = State.InjectBy
        };
        return Show(next);
    }

    [On]
    public Task OnMakeOversized(BoundaryMakeOversized signal)
    {
        _ = signal;
        var next = new BoundaryTestState
        {
            Count = State.Count,
            LastBy = State.LastBy,
            Oversized = true,
            InjectBy = State.InjectBy
        };
        return Show(next);
    }

    [On]
    public Task OnMakeInjectBy(BoundaryMakeInjectBy signal)
    {
        _ = signal;
        var next = new BoundaryTestState
        {
            Count = State.Count,
            LastBy = State.LastBy,
            Oversized = State.Oversized,
            InjectBy = true
        };
        return Show(next);
    }

    [On]
    public Task OnNoOp(BoundaryNoOp signal)
    {
        _ = signal;
        return Show(State);
    }
}

public sealed class ScreenBoundaryFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public ScreenBoundaryFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T2_Screen_Activate_ObsoleteGeneration_ThrowsActivationRefusedStale()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-stale");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Advance screen to generation 2
        await screen.Request("boundary-test.inc", JsonSerializer.SerializeToElement(new BoundaryInc()));
        await os.Idle();

        var gen2View = await screen.Read();
        Assert.NotNull(gen2View);
        Assert.Equal(2L, gen2View.Generation);

        // Attempt activation with obsolete generation 1L
        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", 1L, "btn-inc"));

        Assert.Equal(ActivationRefusedException.Stale, ex.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_ZeroGeneration_ThrowsActivationRefusedStale()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-zero");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1L, view.Generation);

        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", 0L, "btn-inc"));

        Assert.Equal(ActivationRefusedException.Stale, ex.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_NegativeGeneration_ThrowsActivationRefusedStale()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-neg");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);

        var ex1 = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", -1L, "btn-inc"));
        Assert.Equal(ActivationRefusedException.Stale, ex1.Reason);

        var exMin = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", long.MinValue, "btn-inc"));
        Assert.Equal(ActivationRefusedException.Stale, exMin.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_FutureGeneration_ThrowsActivationRefusedStale()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-future");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1L, view.Generation);

        var ex99 = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", 99L, "btn-inc"));
        Assert.Equal(ActivationRefusedException.Stale, ex99.Reason);

        var exMax = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", long.MaxValue, "btn-inc"));
        Assert.Equal(ActivationRefusedException.Stale, exMax.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_MismatchingAlias_ThrowsActivationRefusedStale()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-mismatch-alias");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1L, view.Generation);

        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.foreign_alias", 1L, "btn-inc"));

        Assert.Equal(ActivationRefusedException.Stale, ex.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_NullAlias_ThrowsArgumentNullException()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-null-alias");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            screen.Activate(null!, 1L, "btn-inc"));
    }

    [Fact]
    public async Task T2_Screen_Activate_NullNodeKey_ThrowsArgumentNullException()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-null-key");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            screen.Activate("app.boundary_screen", 1L, null!));
    }

    [Fact]
    public async Task T2_Screen_Activate_NonExistentNodeKey_ThrowsActivationRefusedUnknownNode()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-unknown-node");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);

        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", 1L, "non-existent-button"));

        Assert.Equal(ActivationRefusedException.UnknownNode, ex.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_NodeWithoutEmit_ThrowsActivationRefusedNoEmit()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-no-emit");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);

        // fld-no-emit is a Field without submit emit
        var exField = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", 1L, "fld-no-emit"));
        Assert.Equal(ActivationRefusedException.NoEmit, exField.Reason);
    }

    [Fact]
    public async Task T2_Screen_Activate_ForeignEmitOwner_ThrowsActivationRefusedForeignOwner()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-foreign-owner");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);

        // btn-alien specifies Owner = "app.foreign_owner" which does not match Alias "app.boundary_screen"
        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.boundary_screen", 1L, "btn-alien"));

        Assert.Equal(ActivationRefusedException.ForeignOwner, ex.Reason);
    }

    [Fact]
    public async Task T2_Screen_Render_ViewDeclaringByInArgs_ThrowsInvalidOperationException()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-declares-by");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // Initial read is fine
        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Activating btn-inject-by renders view with author-declared 'by', which throws InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.boundary_screen", 1L, "btn-inject-by"));

        Assert.Contains("declares the host-reserved 'by'", ex.Message);

        // Prior view state is preserved intact at generation 1
        var recoveredView = await screen.Read();
        Assert.NotNull(recoveredView);
        Assert.Equal(1L, recoveredView.Generation);
    }

    [Fact]
    public async Task T2_Screen_IdenticalStateRender_PreservesGenerationAndDoesNotPublishViewChanged()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-identical-render");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view1 = await screen.Read();
        Assert.NotNull(view1);
        Assert.Equal(1L, view1.Generation);

        // Dispatch NoOp: re-renders identical view JSON
        await screen.Request("boundary-test.noop", JsonSerializer.SerializeToElement(new BoundaryNoOp()));
        await os.Idle();

        var view2 = await screen.Read();
        Assert.NotNull(view2);
        // Generation must remain 1L because rendered wire text is identical
        Assert.Equal(1L, view2.Generation);

        // Activation using unchanged generation 1L continues to succeed
        await screen.Activate("app.boundary_screen", 1L, "btn-inc");
        await os.Idle();

        var view3 = await screen.Read();
        Assert.NotNull(view3);
        Assert.Equal(2L, view3.Generation);
        Assert.Contains("Count: 1", view3.View.GetRawText());
    }

    [Fact]
    public async Task T2_Screen_Read_MismatchedAlias_ReturnsNullWithoutThrowing()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-read-mismatch");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var match = await screen.Read("app.boundary_screen");
        Assert.NotNull(match);

        var mismatch = await screen.Read("app.other_alias");
        Assert.Null(mismatch);
    }

    [Fact]
    public async Task T2_Screen_Request_NullOrWhitespaceSignal_ThrowsArgumentException()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-empty-signal");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // When dispatched through [OneWayCall] proxy, failure is logged in os.Failures
        await screen.Request("");
        await os.Idle();
        Assert.NotEmpty(os.Failures);
        Assert.Contains(os.Failures, ex => ex is ArgumentException && (ex.Message.Contains("whitespace") || ex.Message.Contains("signal")));

        await screen.Request("   ");
        await os.Idle();
        Assert.Contains(os.Failures, ex => ex is ArgumentException && (ex.Message.Contains("whitespace") || ex.Message.Contains("signal")));
    }

    [Fact]
    public async Task T2_Screen_Request_UnregisteredReactionSignal_ThrowsArgumentException()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-unknown-signal");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        await screen.Request("unknown.signal.not_registered");
        await os.Idle();

        Assert.NotEmpty(os.Failures);
        Assert.Contains(os.Failures, ex => ex is ArgumentException && ex.Message.Contains("has no [On] reaction"));
    }

    [Fact]
    public async Task T2_Screen_StoreState_Exceeding2MbCap_ThrowsInvalidOperationException()
    {
        var os = _fixture.OS;
        os.Register<BoundaryTestScreen>();

        var brain = NewBrain("screen-oversized");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.boundary_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1L, view.Generation);

        // Activating btn-oversized triggers reaction that renders view exceeding 2 MB -> Throws InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.boundary_screen", 1L, "btn-oversized"));

        Assert.Contains("exceeds the 2097152-byte cap", ex.Message);

        // Previous view remains active and valid
        var preservedView = await screen.Read();
        Assert.NotNull(preservedView);
        Assert.Equal(1L, preservedView.Generation);
    }
}
