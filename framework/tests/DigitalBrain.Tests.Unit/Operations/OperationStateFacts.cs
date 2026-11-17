using DigitalBrain.Confirmation;
using DigitalBrain.Operations;
using DigitalBrain.Rules;
using Xunit;

namespace DigitalBrain.Tests.Unit.Operations;

public sealed class OperationStateFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddSeconds(10);
    private static readonly DateTimeOffset T2 = T0.AddSeconds(20);

    private static Transition<OperationState> Moved(Outcome<OperationState> outcome) => Assert.IsType<Transition<OperationState>>(outcome.Value);

    private static OperationState Initial(string id = "op-1") =>
        new(id, "b1", "caller", "TestPlan", "{}", null, null, OperationPhase.Running, false, 0d, null, T0, T0);

    [Fact]
    public void AwaitApprovalTransitionsRunningToAwaiting()
    {
        var state = Initial();
        var transition = Moved(state.AwaitApproval("plan-1", "guard-abc", T1));
        Assert.Equal(OperationPhase.AwaitingApproval, transition.Next.Phase);
        Assert.Equal("plan-1", transition.Next.ApprovalPlanId);
        Assert.Equal("guard-abc", transition.Next.Guard);
        Assert.Equal(T1, transition.Next.Updated);

        var signal = Assert.Single(transition.Signals);
        var awaiting = Assert.IsType<OperationAwaitingApproval>(signal);
        Assert.Equal(new OperationId("op-1"), awaiting.Operation);
        Assert.Equal("plan-1", awaiting.PlanId);
    }

    [Fact]
    public void AwaitApprovalRejectsIfAlreadyAwaitingOrCancelRequestedOrDone()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;
        Assert.IsType<OperationRefusals.ApprovalAsked>(awaiting.AwaitApproval("plan-2", null, T2).Value);

        var cancelled = state with { CancelRequested = true };
        Assert.IsType<OperationRefusals.CancelPending>(cancelled.AwaitApproval("plan-1", null, T1).Value);

        var done = state with { Phase = OperationPhase.Done };
        Assert.IsType<OperationRefusals.AlreadyDone>(done.AwaitApproval("plan-1", null, T1).Value);
    }

    [Fact]
    public void AnswerApprovedResumesOperation()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;

        var answer = new ConfirmationResult("plan-1", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
        var transition = Moved(awaiting.Answer(answer, T2));
        Assert.Equal(OperationPhase.Running, transition.Next.Phase);
        Assert.Equal(T2, transition.Next.Resumed);
        Assert.Equal(T2, transition.Next.Updated);

        var signal = Assert.Single(transition.Signals);
        var progress = Assert.IsType<OperationProgress>(signal);
        Assert.Equal(0d, progress.Fraction);
    }

    [Fact]
    public void AnswerDeniedCompletesOperationAsFailed()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;

        var answer = new ConfirmationResult("plan-1", ConfirmationOutcome.Denied, ConfirmationDenyReason.ByPerson, false);
        var transition = Moved(awaiting.Answer(answer, T2));
        Assert.Equal(OperationPhase.Done, transition.Next.Phase);
        var failed = Assert.IsType<OperationResult.Failed>(transition.Next.Result);
        Assert.Equal("declined", failed.Code);

        var signal = Assert.Single(transition.Signals);
        Assert.IsType<OperationDone>(signal);
    }

    [Fact]
    public void AnswerExpiredCompletesOperationAsExpired()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;

        var answer = new ConfirmationResult("plan-1", ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired, false);
        var transition = Moved(awaiting.Answer(answer, T2));
        Assert.Equal(OperationPhase.Done, transition.Next.Phase);
        var failed = Assert.IsType<OperationResult.Failed>(transition.Next.Result);
        Assert.Equal("expired", failed.Code);
    }

    [Fact]
    public void AnswerSelfApprovalCompletesOperationAsOnlyPersonCanApprove()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;

        var answer = new ConfirmationResult("plan-1", ConfirmationOutcome.Denied, ConfirmationDenyReason.SelfApproval, false);
        var transition = Moved(awaiting.Answer(answer, T2));
        var failed = Assert.IsType<OperationResult.Failed>(transition.Next.Result);
        Assert.Equal("declined", failed.Code);
        Assert.Equal("Only a person can approve this.", failed.Message);
    }

    [Fact]
    public void AnswerRejectsMismatchedPlanOrNonAwaitingPhase()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;

        var mismatched = new ConfirmationResult("plan-wrong", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
        Assert.IsType<OperationRefusals.PlanMismatch>(awaiting.Answer(mismatched, T2).Value);

        var running = Initial();
        var answer = new ConfirmationResult("plan-1", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
        Assert.IsType<OperationRefusals.NotAwaitingApproval>(running.Answer(answer, T2).Value);
    }

    [Fact]
    public void RequestCancelWithHandlerRunsSetsCancelRequested()
    {
        var state = Initial();
        var transition = Moved(state.RequestCancel(handlerRuns: true, T1));
        Assert.True(transition.Next.CancelRequested);
        Assert.Equal(OperationPhase.Running, transition.Next.Phase);
        Assert.Empty(transition.Signals);
    }

    [Fact]
    public void RequestCancelWithoutHandlerRunsCompletesImmediately()
    {
        var state = Initial();
        var transition = Moved(state.RequestCancel(handlerRuns: false, T1));
        Assert.Equal(OperationPhase.Done, transition.Next.Phase);
        Assert.IsType<OperationResult.Cancelled>(transition.Next.Result);

        var signal = Assert.Single(transition.Signals);
        Assert.IsType<OperationDone>(signal);
    }

    [Fact]
    public void RequestCancelWhileAwaitingApprovalCompletesImmediately()
    {
        var state = Initial();
        var awaiting = Moved(state.AwaitApproval("plan-1", null, T1)).Next;

        var transition = Moved(awaiting.RequestCancel(handlerRuns: true, T2));
        Assert.Equal(OperationPhase.Done, transition.Next.Phase);
        Assert.IsType<OperationResult.Cancelled>(transition.Next.Result);
    }

    [Fact]
    public void CompleteSucceededSetsFractionTo1AndPublishesDone()
    {
        var state = Initial() with { Fraction = 0.5 };
        var transition = Moved(state.Complete(new OperationResult.Succeeded(), T1));
        Assert.Equal(OperationPhase.Done, transition.Next.Phase);
        Assert.Equal(1d, transition.Next.Fraction);
        Assert.IsType<OperationResult.Succeeded>(transition.Next.Result);

        var signal = Assert.Single(transition.Signals);
        var done = Assert.IsType<OperationDone>(signal);
        Assert.IsType<OperationResult.Succeeded>(done.Result);
    }

    [Fact]
    public void CompleteRejectsAlreadyDoneOrNonExistent()
    {
        var done = Initial() with { Phase = OperationPhase.Done };
        Assert.IsType<OperationRefusals.AlreadyDone>(done.Complete(new OperationResult.Succeeded(), T1).Value);

        var empty = new OperationState();
        Assert.IsType<OperationRefusals.NotStarted>(empty.Complete(new OperationResult.Succeeded(), T1).Value);
    }

    [Fact]
    public void ProgressClampsFractionAndPublishesSignal()
    {
        var state = Initial();
        var t1 = Moved(state.Progress(0.75, T1));
        Assert.Equal(0.75, t1.Next.Fraction);
        Assert.Equal(0.75, Assert.IsType<OperationProgress>(Assert.Single(t1.Signals)).Fraction);

        var tClampedHigh = Moved(state.Progress(1.5, T1));
        Assert.Equal(1d, tClampedHigh.Next.Fraction);

        var tClampedLow = Moved(state.Progress(-0.5, T1));
        Assert.Equal(0d, tClampedLow.Next.Fraction);
    }

    [Fact]
    public void ProgressRejectsWhenCancelRequestedOrNotRunning()
    {
        var cancelled = Initial() with { CancelRequested = true };
        Assert.IsType<OperationRefusals.CancelPending>(cancelled.Progress(0.5, T1).Value);

        var awaiting = Moved(Initial().AwaitApproval("plan-1", null, T1)).Next;
        Assert.IsType<OperationRefusals.AwaitingApproval>(awaiting.Progress(0.5, T2).Value);

        var done = Initial() with { Phase = OperationPhase.Done };
        Assert.IsType<OperationRefusals.AlreadyDone>(done.Progress(0.5, T1).Value);
    }
}
