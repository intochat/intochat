using DigitalBrain.Rules;
using DigitalBrain.Flows;
using Xunit;

namespace DigitalBrain.Tests.Unit.Flows;

public sealed class FlowStateFacts
{
    private sealed record TestInner;

    private static Transition<FlowState<TestInner>> Moved(Outcome<FlowState<TestInner>> outcome) => Assert.IsType<Transition<FlowState<TestInner>>>(outcome.Value);

    [Fact]
    public void Start_WhenPending_SetsRunningAndEmitsStarted()
    {
        var state = new FlowState<TestInner>();
        var now = DateTimeOffset.UtcNow;
        var transition = Moved(state.Start("Sample Flow", "user1", null, new TestInner(), now, "trace1", "test.flow"));
        Assert.Equal(FlowStatus.Running, transition.Next.Status);
        Assert.Equal("Sample Flow", transition.Next.Title);
        Assert.Equal("user1", transition.Next.Owner);
        Assert.Equal(now, transition.Next.Started);
        var signal = Assert.IsType<FlowStarted>(Assert.Single(transition.Signals));
        Assert.Equal("Sample Flow", signal.Title);
    }

    [Fact]
    public void Start_WhenNotPending_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        Assert.IsType<FlowRefusals.AlreadyStarted>(running.Start("T2", "O2", null, new TestInner(), now, null, "test").Value);
    }

    [Fact]
    public void RequestCancel_WhenActive_SetsCancelledAndEmitsDone()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var cancel = Moved(running.RequestCancel("user requested", now));
        Assert.Equal(FlowStatus.Cancelled, cancel.Next.Status);
        Assert.Equal(now, cancel.Next.Ended);
        Assert.True(cancel.Next.IsOver);
        var signal = Assert.IsType<FlowDone>(Assert.Single(cancel.Signals));
        Assert.Equal(FlowOutcome.Cancelled, signal.Outcome);
        Assert.Equal("user requested", signal.Detail);
    }

    [Fact]
    public void RequestCancel_WhenOver_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var cancelled = Moved(Moved(new FlowState<TestInner>()
            .Start("T", "O", null, new TestInner(), now, null, "test")).Next
            .RequestCancel("r", now)).Next;

        Assert.IsType<FlowRefusals.AlreadyOver>(cancelled.RequestCancel("again", now).Value);
    }

    [Fact]
    public void StartStep_WhenStepsRemaining_StartsNextStep()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var step1 = Moved(running.StartStep(["stepA", "stepB"], now));
        Assert.Equal("stepA", step1.Next.Current);
        var signal = Assert.IsType<FlowStep>(Assert.Single(step1.Signals));
        Assert.Equal("stepA", signal.Step);
        Assert.Equal(StepOutcome.Started, signal.Outcome);
    }

    [Fact]
    public void StartStep_WhenNoStepsRemaining_CompletesFlow()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var completed = Moved(running.StartStep([], now));
        Assert.Equal(FlowStatus.Completed, completed.Next.Status);
        Assert.Null(completed.Next.Current);
        Assert.Equal(now, completed.Next.Ended);
        var signal = Assert.IsType<FlowDone>(Assert.Single(completed.Signals));
        Assert.Equal(FlowOutcome.Completed, signal.Outcome);
    }

    [Fact]
    public void Complete_WhenStepMatches_AdvancesStepAndEmits()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        var result = new StepResult<TestInner>(new TestInner());
        var completed = Moved(withStep.Complete("stepA", result));
        Assert.Null(completed.Next.Current);
        Assert.Contains("stepA", completed.Next.Done);
        var signal = Assert.IsType<FlowStep>(Assert.Single(completed.Signals));
        Assert.Equal("stepA", signal.Step);
        Assert.Equal(StepOutcome.Completed, signal.Outcome);
    }

    [Fact]
    public void Complete_WhenStepDoesNotMatch_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        Assert.IsType<FlowRefusals.StepNotRunning>(withStep.Complete("differentStep", new StepResult<TestInner>(new TestInner())).Value);
    }

    [Fact]
    public void Wait_WhenStepMatches_EntersWaitingAndEmits()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        var waiting = new Waiting("stepA", WaitReason.Approval, now.AddMinutes(5), "plan123");
        var wait = Moved(withStep.Wait("stepA", new StepResult<TestInner>(new TestInner(), waiting), waiting, "plan123"));
        Assert.Equal(FlowStatus.Waiting, wait.Next.Status);
        Assert.Equal("plan123", wait.Next.Waiting?.ConfirmationPlanId);
        var signal = Assert.IsType<FlowWaiting>(Assert.Single(wait.Signals));
        Assert.Equal("plan123", signal.ConfirmationPlanId);
    }

    [Fact]
    public void Fail_WhenActive_SetsFailedAndEmits()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        var fail = Moved(withStep.Fail("stepA", "something broke", now));
        Assert.Equal(FlowStatus.Failed, fail.Next.Status);
        Assert.Equal(now, fail.Next.Ended);
        Assert.Equal(2, fail.Signals.Count);
        Assert.IsType<FlowStep>(fail.Signals[0]);
        Assert.IsType<FlowDone>(fail.Signals[1]);
    }

    [Fact]
    public void Interrupted_IncrementsCountAndEmitsInterrupted()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        var interrupted = withStep.Interrupted("stepA", "reboot", now, maxInterruptions: 5);
        Assert.Equal(1, interrupted.Next.Interruptions);
        Assert.Equal(FlowStatus.Running, interrupted.Next.Status);
        var signal = Assert.IsType<FlowStep>(Assert.Single(interrupted.Signals));
        Assert.Equal(StepOutcome.Interrupted, signal.Outcome);
    }

    [Fact]
    public void Interrupted_WhenMaxExceeded_FailsFlow()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next with { Interruptions = 4 };

        var interrupted = withStep.Interrupted("stepA", "reboot again", now, maxInterruptions: 5);
        Assert.Equal(5, interrupted.Next.Interruptions);
        Assert.Equal(FlowStatus.Failed, interrupted.Next.Status);
        Assert.Equal(now, interrupted.Next.Ended);
        Assert.Equal(2, interrupted.Signals.Count);
    }

    [Fact]
    public void Resolve_ApprovedOrArrived_ResumesRunningAndCompletesStep()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;
        var waiting = new Waiting("stepA", WaitReason.Approval, now.AddMinutes(5), "plan1");
        var inWait = Moved(withStep.Wait("stepA", new StepResult<TestInner>(new TestInner(), waiting), waiting, "plan1")).Next;

        var resolve = Moved(inWait.Resolve("stepA", WaitOutcome.Approved, "person1", now));
        Assert.Equal(FlowStatus.Running, resolve.Next.Status);
        Assert.Null(resolve.Next.Waiting);
        Assert.Contains("stepA", resolve.Next.Done);
        Assert.Equal(2, resolve.Signals.Count);
        var res = Assert.IsType<FlowResolve>(resolve.Signals[0]);
        Assert.Equal(WaitOutcome.Approved, res.Outcome);
        Assert.Equal("person1", res.By);
    }

    [Fact]
    public void Resolve_DeniedOrCancelled_CancelsFlow()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;
        var waiting = new Waiting("stepA", WaitReason.Approval, now.AddMinutes(5), "plan1");
        var inWait = Moved(withStep.Wait("stepA", new StepResult<TestInner>(new TestInner(), waiting), waiting, "plan1")).Next;

        var resolve = Moved(inWait.Resolve("stepA", WaitOutcome.Denied, "person1", now));
        Assert.Equal(FlowStatus.Cancelled, resolve.Next.Status);
        Assert.Null(resolve.Next.Waiting);
        Assert.Equal(now, resolve.Next.Ended);
    }

    [Fact]
    public void Relate_AddsRelationAndEmits()
    {
        var state = new FlowState<TestInner>();
        var rel = new FlowRelation("flow2", RelationKind.Blocks, null);
        var transition = Moved(state.Relate(rel));
        Assert.Contains(rel, transition.Next.Relations);
        var signal = Assert.IsType<FlowRelated>(Assert.Single(transition.Signals));
        Assert.Equal(rel, signal.Relation);
    }

    [Fact]
    public void Relate_WhenAlreadyPresent_ReturnsNull()
    {
        var rel = new FlowRelation("flow2", RelationKind.Blocks, null);
        var state = Moved(new FlowState<TestInner>().Relate(rel)).Next;
        Assert.IsType<FlowRefusals.AlreadyRelated>(state.Relate(rel).Value);
    }

    [Fact]
    public void Skip_MarksStepDoneAndEmitsSkipped()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA", "stepB"], now)).Next;

        var result = StepResult<TestInner>.Skip(new TestInner(), "condition not met");
        var transition = Moved(withStep.Skip("stepA", result, result.Reason));
        Assert.Contains("stepA", transition.Next.Done);
        Assert.Null(transition.Next.Current);
        var signal = Assert.IsType<FlowStep>(Assert.Single(transition.Signals));
        Assert.Equal(StepOutcome.Skipped, signal.Outcome);
        Assert.Equal("condition not met", signal.Detail);

        var nextStep = Moved(transition.Next.StartStep(["stepA", "stepB"], now));
        Assert.Equal("stepB", nextStep.Next.Current);
    }

    [Fact]
    public void Repeat_KeepsStepNotDoneAndEmitsRepeated()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        var result = StepResult<TestInner>.Repeat(new TestInner());
        var transition = Moved(withStep.Repeat("stepA", result));
        Assert.DoesNotContain("stepA", transition.Next.Done);
        Assert.Null(transition.Next.Current);
        var signal = Assert.IsType<FlowStep>(Assert.Single(transition.Signals));
        Assert.Equal(StepOutcome.Repeated, signal.Outcome);

        var nextStep = Moved(transition.Next.StartStep(["stepA"], now));
        Assert.Equal("stepA", nextStep.Next.Current);
    }

    [Fact]
    public void WaitForChildren_TransitionsToWaitingWithReasonChildren()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Moved(new FlowState<TestInner>().Start("T", "O", null, new TestInner(), now, null, "test")).Next;
        var withStep = Moved(running.StartStep(["stepA"], now)).Next;

        var result = StepResult<TestInner>.WaitForChildren(new TestInner(), ["child-run-1", "child-run-2"]);
        var transition = Moved(withStep.Wait("stepA", result, result.Wait!, null));
        Assert.Equal(FlowStatus.Waiting, transition.Next.Status);
        Assert.NotNull(transition.Next.Waiting);
        Assert.Equal(WaitReason.Children, transition.Next.Waiting!.Reason);
        Assert.Equal(["child-run-1", "child-run-2"], transition.Next.Waiting!.Children);

        var resolve = Moved(transition.Next.Resolve("stepA", WaitOutcome.Arrived, null, now));
        Assert.Equal(FlowStatus.Running, resolve.Next.Status);
        Assert.Contains("stepA", resolve.Next.Done);
    }
}
