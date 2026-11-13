using IntoChat.OS.Modules.Work;

namespace IntoChat.OS.Tests.Unit.Modules.Work;

public sealed class WorkDomainFacts
{
    [Fact]
    public void WorkRequestValidatesRequiredFields()
    {
        var valid = new WorkRequest("w1", "Change unit X", "unit.x", WorkKind.Change, "user1");
        valid.Validate();

        var noId = new WorkRequest("", "Goal", "unit.x");
        Assert.Throws<ArgumentException>(noId.Validate);

        var noGoal = new WorkRequest("w1", "   ", "unit.x");
        Assert.Throws<ArgumentException>(noGoal.Validate);
    }

    [Fact]
    public void WorkPlanHoldsDecomposedTasks()
    {
        var plan = new WorkPlan(
            PlanId: "p1",
            Title: "Migrate database",
            Goal: "Perform schema migration",
            Tasks:
            [
                new WorkTask("t1", "Backup database", "flow.backup", []),
                new WorkTask("t2", "Apply migrations", "flow.migrate", ["t1"]),
            ]);

        Assert.Equal("p1", plan.PlanId);
        Assert.Equal(2, plan.Tasks.Count);
        Assert.Empty(plan.Tasks[0].DependsOn);
        Assert.Equal("t1", Assert.Single(plan.Tasks[1].DependsOn));
    }

    [Fact]
    public void WorkStageTransitionRecordsStageProgression()
    {
        var now = DateTimeOffset.UtcNow;
        var transition = new WorkStageTransition(
            WorkId: "w1",
            FromStage: WorkStage.Pending,
            ToStage: WorkStage.Author,
            Outcome: WorkStageOutcome.InProgress,
            TransitionedAt: now,
            Detail: "Starting authoring");

        Assert.Equal("w1", transition.WorkId);
        Assert.Equal(WorkStage.Pending, transition.FromStage);
        Assert.Equal(WorkStage.Author, transition.ToStage);
        Assert.Equal(WorkStageOutcome.InProgress, transition.Outcome);
        Assert.Equal(now, transition.TransitionedAt);
    }
}
