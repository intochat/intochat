using DigitalBrain.Rules;
using DigitalBrain.Confirmation;
using Xunit;

namespace DigitalBrain.Tests.Unit.Confirmation;

public sealed class ConfirmationStateFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(1);

    private static ConfirmationPlan Plan(string id = "plan-1", bool destructive = true, TimeSpan? expiry = null) =>
        new(id, ConfirmationAction.Delete, destructive, "Title", "Body", "Yes", "No", "Target")
        {
            Expiry = expiry ?? TimeSpan.FromMinutes(2)
        };

    private static ConfirmationRequested[] Pending(ConfirmationState s) =>
        s.Entries.Where(e => e.Result is null).Select(e => e.Request).ToArray();

    private static readonly ConfirmationPrincipal Requester = ConfirmationPrincipal.App("app-1");

    private static Transition<ConfirmationState> Moved(Outcome<ConfirmationState> outcome) => Assert.IsType<Transition<ConfirmationState>>(outcome.Value);

    [Fact]
    public void RequestAddsPendingEntryWhenConfirmationRequired()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-1");

        var transition = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.DestructiveOnly, T0));
        Assert.Null(transition.Next.Known(plan.Id));

        var requested = Assert.IsType<ConfirmationRequested>(Assert.Single(transition.Signals));
        Assert.Equal("cf-1", requested.PlanId);

        var pending = Pending(transition.Next);
        Assert.Single(pending);
        Assert.Equal("cf-1", pending[0].Plan.Id);
    }

    [Fact]
    public void RequestAutoApprovesWhenPolicyNever()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-auto");

        var transition = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Never, T0));
        Assert.NotNull(transition.Next.Known(plan.Id));
        Assert.Equal(ConfirmationOutcome.Approved, transition.Next.Known(plan.Id)!.Outcome);
        Assert.True(transition.Next.Known(plan.Id)!.Skipped);

        var resolved = Assert.IsType<ConfirmationResolved>(Assert.Single(transition.Signals));
        Assert.Equal(ConfirmationOutcome.Approved, resolved.Outcome);
        Assert.True(resolved.Skipped);
        Assert.Empty(Pending(transition.Next));
    }

    [Fact]
    public void RequestEnforces20PendingLimit()
    {
        var state = new ConfirmationState();
        for (var i = 0; i < 20; i++)
        {
            var plan = Plan($"cf-{i}");
            var t = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0));
            state = t.Next;
        }

        Assert.Equal(20, Pending(state).Length);

        var plan21 = Plan("cf-21");
        Assert.IsType<ConfirmationRefusals.TooManyWaiting>(
            state.Request(plan21, Requester, "caller-1", ConfirmationPolicy.Always, T0).Value);
    }

    [Fact]
    public void RequestEnforces128MaxIdLength()
    {
        var state = new ConfirmationState();
        var longId = new string('x', 129);
        var plan = Plan(longId);

        Assert.Throws<ArgumentException>(() => state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0));
    }

    [Fact]
    public void RequestCapsExpiryAt5Minutes()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-long-expiry", expiry: TimeSpan.FromHours(1));

        var transition = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0));
        var entry = Pending(transition.Next)[0];
        Assert.Equal(T0.AddMinutes(5), entry.ExpiresAt);
    }

    [Fact]
    public void RequestIsIdempotentForSameCaller()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-idemp");

        var t1 = Moved(state.Request(plan, Requester, "caller-same", ConfirmationPolicy.Always, T0));
        state = t1.Next;

        var t2 = state.Request(plan, Requester, "caller-same", ConfirmationPolicy.Always, T0);
        Assert.IsType<ConfirmationRefusals.AlreadyAsked>(t2.Value);
    }

    [Fact]
    public void RequestRejectsSameIdFromDifferentCaller()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-diff");

        var t1 = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0));
        state = t1.Next;

        Assert.IsType<ConfirmationRefusals.AlreadyRequested>(
            state.Request(plan, Requester, "caller-2", ConfirmationPolicy.Always, T0).Value);
    }

    [Fact]
    public void AnswerResolvesPendingConfirmation()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-ans");
        state = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0)).Next;

        var approver = ConfirmationPrincipal.Person("user-1");
        var transition = Moved(state.Answer("cf-ans", approve: true, Answerer.Person("user-1"), T1));
        Assert.NotNull(transition.Next.Known("cf-ans"));
        Assert.Equal(ConfirmationOutcome.Approved, transition.Next.Known("cf-ans")!.Outcome);
        Assert.Empty(Pending(transition.Next));

        var resolved = Assert.IsType<ConfirmationResolved>(Assert.Single(transition.Signals));
        Assert.Equal(ConfirmationOutcome.Approved, resolved.Outcome);
        Assert.Equal(approver, resolved.Approver);
    }

    [Fact]
    public void AnswerRejectsSelfApprovalByApp()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-self");
        state = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0)).Next;

        var appApprover = Requester;
        var transition = Moved(state.Answer("cf-self", approve: true, Answerer.From(appApprover), T1));
        Assert.NotNull(transition.Next.Known("cf-self"));
        Assert.Equal(ConfirmationOutcome.Denied, transition.Next.Known("cf-self")!.Outcome);
        Assert.Equal(ConfirmationDenyReason.SelfApproval, transition.Next.Known("cf-self")!.Reason);
    }

    [Fact]
    public void WithdrawCancelsPendingConfirmation()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-with");
        state = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0)).Next;

        var transition = Moved(state.Withdraw("cf-with", T1));
        Assert.Equal(ConfirmationOutcome.Denied, transition.Next.Known("cf-with")?.Outcome);
        Assert.Equal(ConfirmationDenyReason.PlanInvalid, transition.Next.Known("cf-with")?.Reason);
        Assert.Empty(Pending(transition.Next));
    }

    [Fact]
    public void ExpireExpiresTimedOutConfirmations()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-exp", expiry: TimeSpan.FromMinutes(1));
        state = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0)).Next;

        var transition = Moved(state.Expire("cf-exp", T0.AddMinutes(2)));
        Assert.Equal(ConfirmationOutcome.Expired, transition.Next.Known("cf-exp")?.Outcome);
        Assert.Equal(ConfirmationDenyReason.Expired, transition.Next.Known("cf-exp")?.Reason);
        Assert.Empty(Pending(transition.Next));
    }

    [Fact]
    public void RequestPreserves48HourExpiryForRequiresPersonAndCapsAt7Days()
    {
        var state = new ConfirmationState();
        var plan48h = Plan("cf-48h", expiry: TimeSpan.FromHours(48)) with { RequiresPerson = true };

        var t48 = Moved(state.Request(plan48h, Requester, "flow-1", ConfirmationPolicy.Always, T0));
        var entry48 = Pending(t48.Next)[0];
        Assert.Equal(TimeSpan.FromHours(48), entry48.Plan.Expiry);
        Assert.Equal(T0.AddHours(48), entry48.ExpiresAt);

        var plan14d = Plan("cf-14d", expiry: TimeSpan.FromDays(14)) with { RequiresPerson = true };
        var t14 = Moved(t48.Next.Request(plan14d, Requester, "flow-1", ConfirmationPolicy.Always, T0));
        var entry14 = Pending(t14.Next).Single(e => e.PlanId == "cf-14d");
        Assert.Equal(TimeSpan.FromDays(7), entry14.Plan.Expiry);
        Assert.Equal(T0.AddDays(7), entry14.ExpiresAt);
    }

    [Fact]
    public void RequestRejectsSameIdWithMismatchedShapeFromSameCaller()
    {
        var state = new ConfirmationState();
        var original = Plan("cf-squat") with
        {
            RequiresPerson = false,
            Kind = "benign",
            Subject = "item-1",
        };
        state = Moved(state.Request(original, Requester, "caller-1", ConfirmationPolicy.Always, T0)).Next;

        // Mismatched RequiresPerson
        Assert.IsType<ConfirmationRefusals.AlreadyRequested>(
            state.Request(original with { RequiresPerson = true }, Requester, "caller-1", ConfirmationPolicy.Always, T0).Value);

        // Mismatched Kind
        Assert.IsType<ConfirmationRefusals.AlreadyRequested>(
            state.Request(original with { Kind = "pr-merge" }, Requester, "caller-1", ConfirmationPolicy.Always, T0).Value);

        // Mismatched Subject
        Assert.IsType<ConfirmationRefusals.AlreadyRequested>(
            state.Request(original with { Subject = "item-2" }, Requester, "caller-1", ConfirmationPolicy.Always, T0).Value);
    }

    [Fact]
    public void BoundNeverEvictsPersonAnsweredEntriesWithUnaskedEntries()
    {
        var state = new ConfirmationState();
        var approver = ConfirmationPrincipal.Person("alice");

        // Record 5 person-answered plans
        for (var i = 0; i < 5; i++)
        {
            var p = Plan($"answered-{i}") with { RequiresPerson = true };
            state = Moved(state.Request(p, Requester, "flow", ConfirmationPolicy.Always, T0)).Next;
            state = Moved(state.Answer($"answered-{i}", approve: true, Answerer.From(approver, direct: true), T1)).Next;
        }

        // Flood with 60 unasked (Skipped = true) entries under Never policy
        for (var i = 0; i < 60; i++)
        {
            var unasked = Plan($"unasked-{i}", destructive: false);
            state = Moved(state.Request(unasked, Requester, "app", ConfirmationPolicy.DestructiveOnly, T1)).Next;
        }

        Assert.Equal(ConfirmationState.MaxEntries, state.Entries.Length);
        for (var i = 0; i < 5; i++)
        {
            var known = state.Known($"answered-{i}");
            Assert.NotNull(known);
            Assert.False(known.Skipped);
            Assert.Equal(ConfirmationOutcome.Approved, known.Outcome);
        }
    }

    [Fact]
    public void RequestCapsPendingPerRaiserAt20AndAllowsDistinctRaisers()
    {
        var state = new ConfirmationState();
        var raiserOne = ConfirmationPrincipal.App("app-1");
        var raiserTwo = ConfirmationPrincipal.App("app-2");

        for (var i = 0; i < ConfirmationState.PendingLimit; i++)
        {
            var transition = Moved(state.Request(Plan($"r1-{i}"), raiserOne, "raiser-1", ConfirmationPolicy.Always, T0));
            state = transition.Next;
        }

        Assert.IsType<ConfirmationRefusals.TooManyWaiting>(
            state.Request(Plan("r1-over"), raiserOne, "raiser-1", ConfirmationPolicy.Always, T0).Value);

        var secondRaiser = Moved(state.Request(Plan("r2-0"), raiserTwo, "raiser-2", ConfirmationPolicy.Always, T0));
        Assert.Equal(ConfirmationState.PendingLimit + 1, Pending(secondRaiser.Next).Length);
    }

    [Fact]
    public void AnswerWhenRequiresPersonAndNotPersonIsRefusedWithoutPoisoningPendingPlan()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-human-only") with { RequiresPerson = true };
        state = Moved(state.Request(plan, Requester, "flow-1", ConfirmationPolicy.Always, T0)).Next;
        var agentApprover = Answerer.Agent("agent-1");
        var relayedPerson = Answerer.RelayedPerson("alice");

        Assert.IsType<ConfirmationRefusals.PersonOnly>(
            state.Answer("cf-human-only", approve: true, agentApprover, T1).Value);
        Assert.IsType<ConfirmationRefusals.PersonOnly>(
            state.Answer("cf-human-only", approve: false, agentApprover, T1).Value);
        Assert.IsType<ConfirmationRefusals.PersonOnly>(
            state.Answer("cf-human-only", approve: true, relayedPerson, T1).Value);

        Assert.Single(Pending(state));
        Assert.Null(state.Known("cf-human-only"));
    }

    [Fact]
    public void AnswerWhenNotPersonDeniesRecordsSelfApprovalNotByPerson()
    {
        var state = new ConfirmationState();
        var plan = Plan("cf-nonperson-deny") with { RequiresPerson = false };
        state = Moved(state.Request(plan, Requester, "caller-1", ConfirmationPolicy.Always, T0)).Next;
        var agentApprover = Answerer.Agent("agent-1");

        var transition = Moved(state.Answer("cf-nonperson-deny", approve: false, agentApprover, T1));
        Assert.NotNull(transition.Next.Known("cf-nonperson-deny"));
        Assert.Equal(ConfirmationOutcome.Denied, transition.Next.Known("cf-nonperson-deny")!.Outcome);
        Assert.Equal(ConfirmationDenyReason.SelfApproval, transition.Next.Known("cf-nonperson-deny")!.Reason);
    }

    [Fact]
    public void AnswererOfRejectsForeignBrainAndDerivesDirectPersonAuthority()
    {
        var brain = new BrainId("b1");
        var other = new BrainId("b2");
        var alice = new DigitalBrain.Access.Actor.Person("alice", "acc-1");
        var neuron = new NeuronId(brain, "flow.x", "run-1");

        Assert.Throws<UnauthorizedAccessException>(() =>
            Answerer.Of(new DigitalBrain.Access.Caller(alice, other), brain));
        Assert.True(Answerer.Of(new DigitalBrain.Access.Caller(alice, brain), brain).IsPerson);
        Assert.False(Answerer.Of(new DigitalBrain.Access.Caller(alice, brain, via: neuron), brain).IsPerson);
        Assert.False(Answerer.Of(new DigitalBrain.Access.Caller(alice, brain, conversation: "conv-1"), brain).IsPerson);
        Assert.False(Answerer.Of(new DigitalBrain.Access.Caller(new DigitalBrain.Access.Actor.Agent(alice), brain), brain).IsPerson);
        Assert.Throws<UnauthorizedAccessException>(() => Answerer.Of(null, brain));
    }
}
