using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier3_CrossFeature;

public sealed class TestAuditedCounterState
{
    public int Count { get; init; }
    public string? LastBy { get; init; }
    public bool MaintenancePerformed { get; init; }
}

[Signal("test-counter.increment")]
public sealed record TestCounterIncrement(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("test-counter.maintenance")]
public sealed record TestCounterMaintenance : Signal;

/*
# TestAuditedCounter
Screen for testing EdgeAuthority caller propagation and access boundaries.
*/
[Neuron("app.audited_counter")]
public sealed class TestAuditedCounterScreen : Screen<TestAuditedCounterState>
{
    protected override View Render(TestAuditedCounterState state)
    {
        return new View(
            "AuditedCounter",
            new Column([
                new Text($"Count: {state.Count}"),
                new Text($"LastBy: {state.LastBy ?? "none"}"),
                new Text($"MaintenancePerformed: {state.MaintenancePerformed}"),
                new Button("btn-increment", "Increment", Sends(new TestCounterIncrement())),
                new Conversation("btn-alien-thread", [], new ComposerNode("btn-alien", new Command("alien", "test-counter.increment", null)))
            ]));
    }

    [On]
    public Task OnIncrement(TestCounterIncrement signal)
    {
        var next = new TestAuditedCounterState
        {
            Count = State.Count + 1,
            LastBy = signal.By ?? Context.Caller?.ToString() ?? "unknown",
            MaintenancePerformed = State.MaintenancePerformed
        };
        return Show(next);
    }

    [On]
    public Task OnMaintenance(TestCounterMaintenance signal)
    {
        _ = signal;
        var next = new TestAuditedCounterState
        {
            Count = State.Count,
            LastBy = State.LastBy,
            MaintenancePerformed = true
        };
        return Show(next);
    }
}

public sealed class ScreenEdgeAuthorityFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public ScreenEdgeAuthorityFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T3_Screen_EdgeAuthority_DirectPersonCaller_ActivatesNode_PropagatesIdentityIntoViewModelAndIncrementsGeneration()
    {
        var os = _fixture.OS;
        os.Register<TestAuditedCounterScreen>();

        var brain = NewBrain("screen-edge-direct");
        using var person = os.Edge.Enter(os.Edge.Person("alice@intochat.io", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.audited_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        // Initial read
        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Mint direct Person caller
        var caller = CreatePersonCaller(_fixture.Edge, principal: "alice@intochat.io", account: "acct_1", brain: brain);
        Assert.False(caller.Inside);

        await screen.Click("btn-increment");
        await os.Idle();

        var updatedView = await screen.Read();
        Assert.NotNull(updatedView);
        Assert.Equal(2L, updatedView.Generation);

        var rawText = updatedView.View.GetRawText();
        Assert.Contains("Count: 1", rawText);
        Assert.Contains("LastBy: alice@intochat.io", rawText);
    }

    [Fact]
    public void T3_Screen_EdgeAuthority_ForeignBrainCaller_IsRefusedAtAccessGate_WithOutsideBrainDenial()
    {
        var edge = _fixture.Edge;
        var brainA = NewBrain("brain-alpha");
        var brainB = NewBrain("brain-beta");
        var screenIdA = new NeuronId(brainA, "app.audited_counter", "default");

        // Caller belongs to Brain B attempting to call Owner operation on Brain A
        var foreignCaller = CreatePersonCaller(edge, principal: "mallory", account: "acct_evil", brain: brainB);
        var decision = AccessPolicy.Decide(foreignCaller, screenIdA, OperationAccess.Owner);

        Assert.False(decision.Allowed);
        Assert.Equal(AccessDenial.OutsideBrain, decision.Denial);
        Assert.Contains("may not call a neuron of brain", decision.Explanation);
    }

    [Fact]
    public async Task T3_Screen_EdgeAuthority_CommandToAnUnshownChild_ThrowsActivationRefusedExceptionUnknownChild()
    {
        var os = _fixture.OS;
        os.Register<TestAuditedCounterScreen>();

        var brain = NewBrain("screen-foreign-owner");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.audited_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // "btn-alien" commands the child "alien", which the view of "app.audited_counter" does not show
        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Click("btn-alien"));

        Assert.Equal(ActivationRefusedException.UnknownChild, ex.Reason);

        // View remains unchanged
        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1L, view.Generation);
    }

    [Fact]
    public async Task T3_Screen_EdgeAuthority_EnterInProcess_ElevatesHostTurns_BypassingExternalRestrictions()
    {
        var os = _fixture.OS;
        os.Register<TestAuditedCounterScreen>();

        var brain = NewBrain("screen-host-turn");
        var screenId = new NeuronId(brain, "app.audited_counter", "default");
        var screen = os.Get<IScreen>(screenId);
        var edge = _fixture.Edge;

        Assert.False(edge.IsKernelActive);

        // Simulated internal host maintenance turn. EnterInProcess no longer lends an unstamped call the kernel (task 1: no
        // implicit kernel); the host turn enters the kernel of the brain explicitly.
        using (edge.EnterKernel(brain))
        {
            Assert.True(edge.IsKernelActive);

            await screen.Click("btn-increment");
            await os.Idle();
        }

        Assert.False(edge.IsKernelActive);

        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(2L, view.Generation);
    }

    [Fact]
    public async Task T3_Screen_EdgeAuthority_MultiTurnSequentialCallers_UpdatesViewModelAuditTrailMonotonically()
    {
        var os = _fixture.OS;
        os.Register<TestAuditedCounterScreen>();

        var brain = NewBrain("screen-multi-turn");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.audited_counter", "default");
        var screen = os.Get<IScreen>(screenId);

        string[] actors = ["alice@intochat.io", "bob@intochat.io", "charlie@intochat.io"];
        long currentGen = 1L;

        foreach (var actor in actors)
        {
            using (os.Edge.Enter(os.Edge.Person(actor, "acct_" + actor, brain))) { await screen.Click("btn-increment"); }
            await os.Idle();

            currentGen++;
            var currentView = await screen.Read();
            Assert.NotNull(currentView);
            Assert.Equal(currentGen, currentView.Generation);
            Assert.Contains($"LastBy: {actor}", currentView.View.GetRawText());
            Assert.Contains($"Count: {currentGen - 1}", currentView.View.GetRawText());
        }
    }
}
