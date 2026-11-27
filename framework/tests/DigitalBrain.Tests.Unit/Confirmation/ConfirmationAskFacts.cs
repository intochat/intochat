using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Rules;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Confirmation;

public sealed class ConfirmationAskFacts
{
    private static readonly BrainId Brain = new("local");
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConfirmationPlanAsk_StampsRequiresPersonAndValidatesOptions()
    {
        var plan = ConfirmationPlan.Ask("ask-1", "Deploy to production?", ["Deploy now", "Stage first", "Cancel"], recommended: 1);

        Assert.True(plan.RequiresPerson);
        Assert.Equal(["Deploy now", "Stage first", "Cancel"], plan.Options);
        Assert.Equal(1, plan.RecommendedOptionIndex);
        Assert.Equal(ConfirmationPlan.AskKind, plan.Kind);

        Assert.Throws<ArgumentException>(() => ConfirmationPlan.Ask("ask-2", "Only one?", ["Only"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfirmationPlan.Ask("ask-3", "Bad rec?", ["A", "B"], recommended: 5));
    }

    [Fact]
    public void ConfirmationState_AnswerOption_RejectsNonPersonAndAcceptsPersonChoice()
    {
        var state = new ConfirmationState();
        var plan = ConfirmationPlan.Ask("ask-1", "Pick strategy", ["Fast", "Thorough"], recommended: 0);
        var requester = ConfirmationPrincipal.Agent("worker-1");

        var requested = Assert.IsType<Transition<ConfirmationState>>(
            state.Request(plan, requester, "worker-1", ConfirmationPolicy.DestructiveOnly, T0).Value);
        state = requested.Next;

        // Non-person callers (agent / app) are strictly refused with PersonOnly.
        var agentAttempt = state.AnswerOption("ask-1", 0, Answerer.Agent("spoof"), T0.AddSeconds(5));
        Assert.IsType<ConfirmationRefusals.PersonOnly>(agentAttempt.Value);

        // Out-of-range option is refused with InvalidOption.
        var person = Answerer.Person("owner");
        var badOption = state.AnswerOption("ask-1", 9, person, T0.AddSeconds(6));
        Assert.IsType<ConfirmationRefusals.InvalidOption>(badOption.Value);

        // Human person selecting option 1 succeeds and records SelectedOptionIndex + SelectedOption.
        var answered = Assert.IsType<Transition<ConfirmationState>>(
            state.AnswerOption("ask-1", 1, person, T0.AddSeconds(10)).Value);
        var result = answered.Next.Known("ask-1");
        Assert.NotNull(result);
        Assert.True(result!.Approved);
        Assert.Equal(1, result.SelectedOptionIndex);
        Assert.Equal("Thorough", result.SelectedOption);

        var signal = Assert.IsType<ConfirmationResolved>(Assert.Single(answered.Signals));
        Assert.Equal(1, signal.SelectedOptionIndex);
        Assert.Equal("Thorough", signal.SelectedOption);
    }

    [Fact]
    public async Task ConfirmationsNeuron_ResolveOption_EnforcesPersonOnlyAtRuntimeBoundary()
    {
        await using var os = TestOS.Local();
        var requests = os.Get<IConfirmationRequests>(IConfirmationRequests.Of(Brain));
        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(Brain));

        var plan = ConfirmationPlan.Ask("ask-runtime-1", "Approve migration?", ["Run online", "Maintenance window"], recommended: 0);
        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), Brain)))
        {
            Assert.Null(await requests.Request(plan));
        }

        // Agent attempting to resolve option is rejected with UnauthorizedAccessException.
        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.ResolveOption("ask-runtime-1", 0));
        }

        // Direct human Person resolves option 1 cleanly.
        ConfirmationResult? outcome;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            await inbox.ResolveOption("ask-runtime-1", 1);
            outcome = await requests.Outcome("ask-runtime-1");
        }
        Assert.NotNull(outcome);
        Assert.Equal(ConfirmationOutcome.Approved, outcome!.Outcome);
        Assert.Equal(1, outcome.SelectedOptionIndex);
        Assert.Equal("Maintenance window", outcome.SelectedOption);
    }
}
