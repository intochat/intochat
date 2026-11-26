using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class LoopBudgetFacts
{
    [Fact]
    public void FindingFailingThreeTimesIsParkedAndNotRetried()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
        var tracker = new LoopBudgetTracker(new LoopBudget(RunsPerDay: 10, TokensPerRun: 100_000, AttemptsPerFinding: 3), clock);
        const string findingKey = "analyzer:HR-Swallow:src/Worker.cs:14";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(tracker.IsParked(findingKey));
            tracker.EnsureCanStartRun(findingKey);
            tracker.RecordRunStarted(findingKey);
            tracker.RecordFindingFailed(findingKey);
        }

        Assert.True(tracker.IsParked(findingKey));

        var ex = Assert.Throws<LoopBudgetExceededException>(() => tracker.EnsureCanStartRun(findingKey));
        Assert.Equal("loop.budget.attempts", ex.Code);

        // Other findings are unaffected.
        tracker.EnsureCanStartRun("analyzer:HR-12:src/Other.cs:9");
    }

    [Fact]
    public void RunExceedingTokenBudgetIsRefusedWithCodedReason()
    {
        var tracker = new LoopBudgetTracker(new LoopBudget(TokensPerRun: 1_000));

        Assert.Equal(600, tracker.RecordTokens("run-1", new InferenceUsage(InputTokens: 400, OutputTokens: 200, TotalTokens: 600)));
        Assert.Equal(900, tracker.RecordTokens("run-1", new InferenceUsage(InputTokens: 200, OutputTokens: 100, TotalTokens: null)));

        var ex = Assert.Throws<LoopBudgetExceededException>(() =>
            tracker.RecordTokens("run-1", new InferenceUsage(InputTokens: 150, OutputTokens: 50, TotalTokens: 200)));
        Assert.Equal("loop.budget.tokens", ex.Code);
    }

    [Fact]
    public void KillSwitchRefusesBeforeStepExecution()
    {
        var tracker = new LoopBudgetTracker(new LoopBudget(KillSwitch: true));

        var stepEx = Assert.Throws<LoopBudgetExceededException>(tracker.EnsureNotKilled);
        Assert.Equal("loop.kill-switch", stepEx.Code);

        var startEx = Assert.Throws<LoopBudgetExceededException>(() => tracker.EnsureCanStartRun("finding-1"));
        Assert.Equal("loop.kill-switch", startEx.Code);

        var tokenEx = Assert.Throws<LoopBudgetExceededException>(() =>
            tracker.RecordTokens("run-1", new InferenceUsage(10, 10, 20)));
        Assert.Equal("loop.kill-switch", tokenEx.Code);
    }

    [Fact]
    public void DailyRunCapRefusesExcessRunsUntilNextDay()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 22, 0, 0, TimeSpan.Zero));
        var tracker = new LoopBudgetTracker(new LoopBudget(RunsPerDay: 2, AttemptsPerFinding: 5), clock);

        tracker.RecordRunStarted("finding-1");
        tracker.RecordRunStarted("finding-2");

        var ex = Assert.Throws<LoopBudgetExceededException>(() => tracker.EnsureCanStartRun("finding-3"));
        Assert.Equal("loop.budget.daily", ex.Code);

        // Advance past UTC midnight into the next day.
        clock.Advance(TimeSpan.FromHours(3));

        tracker.EnsureCanStartRun("finding-3");
        tracker.RecordRunStarted("finding-3");
    }
}
