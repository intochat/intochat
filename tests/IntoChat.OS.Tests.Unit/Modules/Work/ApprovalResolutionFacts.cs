using IntoChat.OS.Modules.Work;

namespace IntoChat.OS.Tests.Unit.Modules.Work;

public sealed class ApprovalResolutionFacts
{
    [Fact]
    public void WorkApprovalTrackerEnforcesSingleResolutionIdempotency()
    {
        var tracker = new WorkApprovalTracker();
        var planId = "plan-123";
        var time1 = DateTimeOffset.UtcNow;
        var time2 = time1 + TimeSpan.FromMinutes(5);

        // First resolution: succeeds
        var resolvedFirst = tracker.TryResolve(planId, ApprovalStatus.Approved, "admin", time1, "Looks good", out var res1);
        Assert.True(resolvedFirst);
        Assert.Equal(planId, res1.PlanId);
        Assert.Equal(ApprovalStatus.Approved, res1.Status);
        Assert.Equal("admin", res1.ResolvedBy);
        Assert.Equal(time1, res1.ResolvedAt);
        Assert.Equal("Looks good", res1.Reason);

        // Second resolution attempt: must be rejected (idempotent, return existing)
        var resolvedSecond = tracker.TryResolve(planId, ApprovalStatus.Denied, "attacker", time2, "Change mind", out var res2);
        Assert.False(resolvedSecond);
        Assert.Equal(ApprovalStatus.Approved, res2.Status);
        Assert.Equal("admin", res2.ResolvedBy);
        Assert.Equal(time1, res2.ResolvedAt);
        Assert.Equal("Looks good", res2.Reason);

        // Tracker knows it's resolved
        Assert.True(tracker.IsResolved(planId));
        Assert.True(tracker.TryGetResolution(planId, out var existing));
        Assert.NotNull(existing);
        Assert.Equal(ApprovalStatus.Approved, existing.Status);
    }

    [Fact]
    public void WorkApprovalTrackerTracksMultipleDistinctPlans()
    {
        var tracker = new WorkApprovalTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.True(tracker.TryResolve("plan-1", ApprovalStatus.Approved, "user1", now, null, out _));
        Assert.True(tracker.TryResolve("plan-2", ApprovalStatus.Denied, "user2", now, "Denied by policy", out _));

        var all = tracker.AllResolutions();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.PlanId == "plan-1" && r.Status == ApprovalStatus.Approved);
        Assert.Contains(all, r => r.PlanId == "plan-2" && r.Status == ApprovalStatus.Denied);
    }
}
