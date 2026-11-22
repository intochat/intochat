using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Flows;
using Xunit;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog.Flows;

public sealed class LoopFindingsAndLessonsFacts
{
    [Fact]
    public void SameErrorTraceSeenThreeTimesStartsOneRun()
    {
        var spans = new[]
        {
            new LoopTraceSpan("t-1", "s-1", "OperatingSystem", "calculator.add", "Error", 1200, "DivideByZeroException"),
            new LoopTraceSpan("t-2", "s-2", "OperatingSystem", "calculator.add", "Error", 1100, "DivideByZeroException"),
            new LoopTraceSpan("t-3", "s-3", "OperatingSystem", "calculator.add", "Error", 1300, "DivideByZeroException"),
            new LoopTraceSpan("t-4", "s-4", "OperatingSystem", "calculator.add", "Ok", 200, null),
        };

        var deduplicator = new FindingDeduplicator();
        var budget = new LoopBudgetTracker(new LoopBudget(RunsPerDay: 10));
        var startedRuns = 0;

        // Batch of 3 identical error spans collapses into 1 finding.
        var batchFindings = LoopFindingSources.FromErrorTraces(spans);
        var single = Assert.Single(batchFindings);
        Assert.Equal("trace:OperatingSystem:calculator.add", single.Key);
        Assert.Contains("seen 3 times", single.Detail, StringComparison.Ordinal);

        // Even if the same error trace arrives across 3 separate poll cycles, FindingDeduplicator admits it only once.
        foreach (var span in spans.Take(3))
        {
            foreach (var finding in deduplicator.Admit(LoopFindingSources.FromErrorTraces([span])))
            {
                budget.RecordRunStarted(finding.Key);
                startedRuns++;
            }
        }

        Assert.Equal(1, startedRuns);
        Assert.True(deduplicator.HasSeen("trace:OperatingSystem:calculator.add"));
    }

    [Fact]
    public void OwedMarkdownParserSelectsOnlyLoopOwnedLines()
    {
        const string sampleOwed = """
            # Owed

            - Legacy circle shims stay for two releases. Owner: the repo owner.
            - Clean up duplicate retry helper in EngineerRun. Owner: loop.
            - Refactor legacy routes after Phase 2. Owner: Phase 2.
            - Remove stale telemetry warning in AspireNeuron. Owner: LOOP.
            - Line with no owner marker at all.
            """;

        var findings = LoopFindingSources.FromOwedMarkdown(sampleOwed);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("owed", f.Source);
            Assert.StartsWith("owed:", f.Key, StringComparison.Ordinal);
        });
        Assert.Contains(findings, f => f.Detail.Contains("EngineerRun", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Detail.Contains("AspireNeuron", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Detail.Contains("Legacy circle shims", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Detail.Contains("Phase 2", StringComparison.Ordinal));

        // Keys are stable across repeated parses.
        var reparsed = LoopFindingSources.FromOwedMarkdown(sampleOwed);
        Assert.Equal(findings.Select(f => f.Key), reparsed.Select(f => f.Key));
    }

    [Fact]
    public void FailingFactsProduceStableFactKeyedFindings()
    {
        var findings = LoopFindingSources.FromFailingFacts(
        [
            ("CalculatorFacts.DivideByZero", "Expected exception"),
            ("CalculatorFacts.DivideByZero", "Duplicate failure"),
            ("AspireFacts.HealthCheck", "Timed out"),
        ]);

        Assert.Equal(2, findings.Count);
        Assert.Equal("fact:CalculatorFacts.DivideByZero", findings[0].Key);
        Assert.Equal("fact:AspireFacts.HealthCheck", findings[1].Key);
    }

    [Fact]
    public async Task FailedAttemptLessonAppearsInSecondAttemptPlanContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new BrainId("loop-test");
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("loop", "loop", brain));

        await using var os = TestOS.Local();
        os.Register<WorkingMemoryNeuron>();

        var memory = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(brain));

        const string findingKey = "trace:OperatingSystem:calculator.add";
        const string whatWasTried = "Guarded divisor in CalculatorApp.Add";
        const string whyItFailed = "NullReferenceException remained when input string was empty";

        await LoopLessons.RecordFailureAsync(memory, findingKey, whatWasTried, whyItFailed, ct);

        // Verify the lesson was recorded both as a repo:os brief and in EnvironmentNotes.
        var snapshot = await memory.Read();
        Assert.Contains(snapshot.Briefs, b => b.Key == $"lesson:{findingKey}" && b.Category == LoopLessons.Category);
        Assert.Contains(snapshot.LiveScratchpad.EnvironmentNotes, note => note.Contains(findingKey, StringComparison.Ordinal));

        // Second attempt: EngineerPlanner hydrates memory brief automatically.
        var planner = new EngineerPlanner(workingMemory: memory);
        var hydrated = await planner.HydrateMemoryBriefAsync(
            repositoryId: "os",
            goal: $"Fix recurring failure for {findingKey}",
            brain: brain,
            workingMemoryOverride: memory,
            ct: ct);

        Assert.Equal($"lesson:{findingKey}", hydrated.BriefKey);
        Assert.NotNull(hydrated.BriefContent);
        Assert.Contains(findingKey, hydrated.BriefContent, StringComparison.Ordinal);
        Assert.Contains(whatWasTried, hydrated.BriefContent, StringComparison.Ordinal);
        Assert.Contains(whyItFailed, hydrated.BriefContent, StringComparison.Ordinal);

        // PlanWithContextAsync also completes cleanly with the hydrated lesson context.
        var plan = await planner.PlanWithContextAsync(
            repositoryId: "os",
            goal: $"Fix recurring failure for {findingKey}",
            allowedExtensions: [".cs"],
            maxChangedFiles: 3,
            brain: brain,
            workingMemoryOverride: memory,
            ct: ct);

        Assert.NotNull(plan);
    }
}
