using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class FlowsIdempotencyFacts
{
    [Fact]
    public void FlowEntryMaintainsResolvedStepsIdempotently()
    {
        var entry = new FlowEntry(
            FlowId: "f1",
            Type: "flow.build",
            Title: "Test",
            Owner: "system",
            TraceId: "t1",
            Status: FlowStatus.Waiting,
            Steps: ["step1", "step2"],
            Done: [],
            Current: "step1",
            Waiting: new Waiting("step1", WaitReason.Approval, null, "plan1"),
            Relations: [],
            ParentFlowId: null,
            Started: DateTimeOffset.UtcNow,
            Ended: null,
            Detail: null);

        Assert.Empty(entry.ResolvedSteps);

        // Record resolution of step1
        var resolved = entry with
        {
            ResolvedStep = "step1",
            ResolvedSteps = ["step1"],
            Waiting = null,
            Current = null,
            Status = FlowStatus.Running,
        };

        Assert.Contains("step1", resolved.ResolvedSteps);
        Assert.Null(resolved.Waiting);
        Assert.Equal("step1", resolved.ResolvedStep);

        // Redelivery of resolution doesn't duplicate
        var redelivered = resolved with
        {
            ResolvedSteps = resolved.ResolvedSteps.Contains("step1", StringComparer.Ordinal)
                ? resolved.ResolvedSteps
                : [.. resolved.ResolvedSteps, "step1"]
        };
        Assert.Single(redelivered.ResolvedSteps);
    }

    [Fact]
    public void FlowEntryMergePreservesResolvedSteps()
    {
        var entry = new FlowEntry(
            FlowId: "f2",
            Type: "flow.plan",
            Title: "Plan",
            Owner: "system",
            TraceId: "t2",
            Status: FlowStatus.Running,
            Steps: ["draft", "approve"],
            Done: ["draft"],
            Current: null,
            Waiting: null,
            Relations: [],
            ParentFlowId: null,
            Started: DateTimeOffset.UtcNow,
            Ended: null,
            Detail: null,
            ResolvedSteps: ["approve"]);

        var head = new FlowHead(["draft", "approve"], "system", "t2", null, entry.Started, "{}", "Plan", FlowStatus.Running, ["draft"], null, null);
        var merged = entry.Merge(head);

        Assert.True(merged.HeadKnown);
        Assert.Equal(["approve"], merged.ResolvedSteps);
    }
}
