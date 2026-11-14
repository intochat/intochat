using DigitalBrain;
using DigitalBrain.Flows;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Tests.Unit.Primitives;

public sealed record SampleState(string Value);

[Signal("test.sample.signal")]
public sealed record SampleSignal(string Name) : Signal;

public sealed class StepResultTransitionsFacts
{
    [Fact]
    public void AdvanceCreatesCompletedStepResultWithoutWait()
    {
        var state = new SampleState("ready");
        var result = state.Advance();

        Assert.Equal("ready", result.Next.Value);
        Assert.Null(result.Wait);
        Assert.Null(result.Emits);
    }

    [Fact]
    public void AdvancePassesSignalsCleanly()
    {
        var state = new SampleState("advanced");
        var signal = new SampleSignal("test");
        var result = state.Advance(signal);

        Assert.Equal("advanced", result.Next.Value);
        Assert.Null(result.Wait);
        Assert.NotNull(result.Emits);
        Assert.Same(signal, Assert.Single(result.Emits));
    }

    [Fact]
    public void WaitExternalCreatesExternalWaitWithOptionalConfirmationPlan()
    {
        var state = new SampleState("waiting-ext");
        var result = state.WaitExternal("step-1");

        Assert.NotNull(result.Wait);
        Assert.Equal("step-1", result.Wait.Step);
        Assert.Equal(WaitReason.External, result.Wait.Reason);
        Assert.Null(result.Wait.ConfirmationPlanId);
        Assert.Null(result.Wait.Expires);

        var resultWithPlan = state.WaitExternal("step-2", "plan-456");
        Assert.NotNull(resultWithPlan.Wait);
        Assert.Equal("step-2", resultWithPlan.Wait.Step);
        Assert.Equal(WaitReason.External, resultWithPlan.Wait.Reason);
        Assert.Equal("plan-456", resultWithPlan.Wait.ConfirmationPlanId);
    }

    [Fact]
    public void WaitApprovalCreatesApprovalWaitWithExpiry()
    {
        var state = new SampleState("waiting-appr");
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        var result = state.WaitApproval("approve-step", expiry, "plan-789");

        Assert.NotNull(result.Wait);
        Assert.Equal("approve-step", result.Wait.Step);
        Assert.Equal(WaitReason.Approval, result.Wait.Reason);
        Assert.Equal(expiry, result.Wait.Expires);
        Assert.Equal("plan-789", result.Wait.ConfirmationPlanId);
    }

    [Fact]
    public void PollAtCreatesScheduleWaitWithWakeTime()
    {
        var state = new SampleState("polling");
        var wakeAt = DateTimeOffset.UtcNow.AddHours(2);
        var result = state.PollAt("poll-step", wakeAt);

        Assert.NotNull(result.Wait);
        Assert.Equal("poll-step", result.Wait.Step);
        Assert.Equal(WaitReason.Schedule, result.Wait.Reason);
        Assert.Equal(wakeAt, result.Wait.Expires);
    }
}
