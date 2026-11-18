using DigitalBrain.Confirmation;
using CsCheck;
using DigitalBrain.Operations;
using DigitalBrain.Rules;
using DigitalBrain.Tests.Unit.Rules;
using Xunit;

namespace DigitalBrain.Tests.Unit.Operations;

/// <summary>
/// Empirical stress-tests and invariant fuzzer for pure OperationState transitions.
/// </summary>
public sealed class OperationStateStressFacts
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static DigitalBrain.Rules.Transition<OperationState> Moved(Outcome<OperationState> outcome) => Assert.IsType<DigitalBrain.Rules.Transition<OperationState>>(outcome.Value);

    private static OperationState CreateInitial(string id = "op-stress-1") =>
        new(id, "b1", "caller", "TestPlan", "{}", null, null, OperationPhase.Running, false, 0d, null, T0, T0);

    private sealed record Step(int Action, double Fraction, bool Matching, ConfirmationDenyReason Reason);

    private static readonly Gen<Step> Steps = Gen.Select(Gen.Int[0, 9], Gen.Double[-2, 2], Gen.Bool, Gen.Bool.Select(static self => self ? ConfirmationDenyReason.SelfApproval : ConfirmationDenyReason.ByPerson),
        static (action, fraction, matching, reason) => new Step(action, fraction, matching, reason));

    private static readonly DateTimeOffset T1 = T0.AddSeconds(1);

    private static Outcome<OperationState> Apply(OperationState state, Step step)
    {
        var plan = step.Matching ? state.ApprovalPlanId ?? "plan-valid" : "plan-wrong";
        return step.Action switch
        {
            0 => state.AwaitApproval(step.Matching ? "plan-valid" : "plan-alt", step.Matching ? "guard" : null, T1),
            1 => state.Answer(new ConfirmationResult(plan, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false), T1),
            2 => state.Answer(new ConfirmationResult(plan, ConfirmationOutcome.Denied, step.Reason, false), T1),
            3 => state.Answer(new ConfirmationResult(plan, ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired, false), T1),
            4 => state.Progress(step.Fraction, T1),
            5 => state.Progress(step.Matching ? double.MaxValue : -100, T1),
            6 => state.RequestCancel(handlerRuns: true, T1),
            7 => state.RequestCancel(handlerRuns: false, T1),
            8 => state.Complete(new OperationResult.Succeeded(), T1),
            _ => state.Complete(new OperationResult.Failed("err", "Fuzz error"), T1),
        };
    }

    private static readonly Type[] Declared = typeof(OperationRefusals).GetNestedTypes();

    [Fact]
    public void EveryMoveKeepsTheInvariantsAndEveryNoIsADeclaredRefusal() =>
        Lifecycle.Holds<OperationState, Step>(CreateInitial(), Steps, static (state, step) => Apply(state, step), static (before, step, outcome) =>
        {
            switch (outcome)
            {
                case DigitalBrain.Rules.Transition<OperationState> moved:
                    var state = moved.Next;
                    Assert.NotEqual(OperationPhase.Done, before.Phase);
                    if (state.Phase == OperationPhase.Done)
                    {
                        Assert.NotNull(state.Result);
                        Assert.Contains(moved.Signals, s => s is OperationDone done && done.Result == state.Result);
                    }
                    else
                    {
                        Assert.Null(state.Result);
                    }
                    if (state.Phase == OperationPhase.AwaitingApproval)
                    {
                        Assert.NotNull(state.ApprovalPlanId);
                        Assert.Contains(moved.Signals, s => s is OperationAwaitingApproval awaiting && awaiting.PlanId == state.ApprovalPlanId);
                    }
                    if (state.Phase == OperationPhase.Running && step.Action is 4 or 5)
                    {
                        Assert.Contains(moved.Signals, s => s is OperationProgress p && p.Fraction == state.Fraction);
                    }
                    Assert.InRange(state.Fraction, 0d, 1d);
                    if (state.Result is OperationResult.Succeeded) { Assert.Equal(1d, state.Fraction); }
                    break;
                case Refusal refusal:
                    Assert.Contains(refusal.GetType(), Declared);
                    break;
                default:
                    Assert.Fail("An outcome with no case.");
                    break;
            }
        }, longest: 50);

    [Fact]
    public void InvalidTransitionMatrix_DoneStateRejectsAllTransitionsPermanently()
    {
        var doneSucceeded = Moved(CreateInitial().Complete(new OperationResult.Succeeded(), T0)).Next;
        var doneCancelled = Moved(CreateInitial().Complete(new OperationResult.Cancelled(), T0)).Next;
        var doneFailed = Moved(CreateInitial().Complete(new OperationResult.Failed("e", "m"), T0)).Next;

        foreach (var done in new[] { doneSucceeded, doneCancelled, doneFailed })
        {
            Assert.Equal(OperationPhase.Done, done.Phase);

            // Attempt Complete
            Assert.IsAssignableFrom<Refusal>(done.Complete(new OperationResult.Succeeded(), T0.AddSeconds(1)).Value);
            Assert.IsAssignableFrom<Refusal>(done.Complete(new OperationResult.Failed("err", "msg"), T0.AddSeconds(1)).Value);
            Assert.IsAssignableFrom<Refusal>(done.Complete(new OperationResult.Cancelled(), T0.AddSeconds(1)).Value);

            // Attempt AwaitApproval
            Assert.IsAssignableFrom<Refusal>(done.AwaitApproval("plan-new", "guard", T0.AddSeconds(1)).Value);

            // Attempt Answer
            var answer = new ConfirmationResult("plan-new", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
            Assert.IsAssignableFrom<Refusal>(done.Answer(answer, T0.AddSeconds(1)).Value);

            // Attempt Progress
            Assert.IsAssignableFrom<Refusal>(done.Progress(0.5, T0.AddSeconds(1)).Value);
            Assert.IsAssignableFrom<Refusal>(done.Progress(1.0, T0.AddSeconds(1)).Value);

            // Attempt Cancel
            Assert.IsAssignableFrom<Refusal>(done.RequestCancel(handlerRuns: true, T0.AddSeconds(1)).Value);
            Assert.IsAssignableFrom<Refusal>(done.RequestCancel(handlerRuns: false, T0.AddSeconds(1)).Value);
        }
    }

    [Fact]
    public void InvalidTransitionMatrix_AwaitingApprovalRejectsInvalidActions()
    {
        var awaiting = Moved(CreateInitial().AwaitApproval("plan-42", "guard-xyz", T0)).Next;
        Assert.Equal(OperationPhase.AwaitingApproval, awaiting.Phase);

        // Cannot await approval again
        Assert.IsAssignableFrom<Refusal>(awaiting.AwaitApproval("plan-43", "guard-new", T0.AddSeconds(1)).Value);

        // Cannot report progress while awaiting approval
        Assert.IsAssignableFrom<Refusal>(awaiting.Progress(0.1, T0.AddSeconds(1)).Value);
        Assert.IsAssignableFrom<Refusal>(awaiting.Progress(0.9, T0.AddSeconds(1)).Value);

        // Answering with mismatched plan ID is rejected
        var wrongPlan = new ConfirmationResult("plan-wrong", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, false);
        Assert.IsAssignableFrom<Refusal>(awaiting.Answer(wrongPlan, T0.AddSeconds(1)).Value);
    }

    [Fact]
    public void InvalidTransitionMatrix_RunningWithCancelRequestedRejectsProgressAndAwaitApproval()
    {
        var cancelled = Moved(CreateInitial().RequestCancel(handlerRuns: true, T0)).Next;
        Assert.Equal(OperationPhase.Running, cancelled.Phase);
        Assert.True(cancelled.CancelRequested);

        // Cannot report progress once cancel is requested
        Assert.IsAssignableFrom<Refusal>(cancelled.Progress(0.5, T0.AddSeconds(1)).Value);

        // Cannot enter awaiting approval once cancel is requested
        Assert.IsAssignableFrom<Refusal>(cancelled.AwaitApproval("plan-1", null, T0.AddSeconds(1)).Value);

        // Subsequent RequestCancel returns null (idempotent reject)
        Assert.IsAssignableFrom<Refusal>(cancelled.RequestCancel(handlerRuns: true, T0.AddSeconds(1)).Value);

        // But Complete can finalize it
        var finalized = Moved(cancelled.Complete(new OperationResult.Cancelled(), T0.AddSeconds(2)));
        Assert.Equal(OperationPhase.Done, finalized.Next.Phase);
        Assert.IsType<OperationResult.Cancelled>(finalized.Next.Result);
    }

    [Fact]
    public void InvalidTransitionMatrix_UninitializedStateRejectsComplete()
    {
        var empty = new OperationState();
        Assert.False(empty.Exists);
        Assert.IsAssignableFrom<Refusal>(empty.Complete(new OperationResult.Succeeded(), T0).Value);
    }

    [Fact]
    public void ConcurrentTransitionRace_ConcurrentCallersConsistentlySealTerminalState()
    {
        const int iterations = 100;
        for (var i = 0; i < iterations; i++)
        {
            var current = CreateInitial($"op-race-{i}");
            var now = T0;
            var completedCount = 0;
            var cancelledCount = 0;

            Parallel.For(0, 40, taskIdx =>
            {
                if (taskIdx < 10)
                {
                    var t = current.Complete(new OperationResult.Succeeded(), now.AddSeconds(taskIdx));
                    if (t.Value is DigitalBrain.Rules.Transition<OperationState> moved && Interlocked.CompareExchange(ref current, moved.Next, current) == current)
                    {
                        Interlocked.Increment(ref completedCount);
                    }
                }
                else if (taskIdx < 20)
                {
                    var t = current.RequestCancel(handlerRuns: false, now.AddSeconds(taskIdx));
                    if (t.Value is DigitalBrain.Rules.Transition<OperationState> moved && Interlocked.CompareExchange(ref current, moved.Next, current) == current)
                    {
                        Interlocked.Increment(ref cancelledCount);
                    }
                }
                else
                {
                    var t = current.Progress(0.1 * (taskIdx - 20), now.AddSeconds(taskIdx));
                    if (t.Value is DigitalBrain.Rules.Transition<OperationState> moved)
                    {
                        Interlocked.CompareExchange(ref current, moved.Next, current);
                    }
                }
            });

            // Once any terminal transition wins, state must be Done and Result must not be null
            if (current.Phase == OperationPhase.Done)
            {
                Assert.NotNull(current.Result);
                // No further transition can succeed
                Assert.IsAssignableFrom<Refusal>(current.Complete(new OperationResult.Succeeded(), now.AddDays(1)).Value);
                Assert.IsAssignableFrom<Refusal>(current.Progress(0.5, now.AddDays(1)).Value);
            }
        }
    }
}
