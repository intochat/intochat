using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class ChangeFlowStagesFacts
{
    [Fact]
    public void ChangeFlowDefinesExactlySevenStages()
    {
        Assert.Equal(7, ChangeFlow.Names.Length);
        Assert.Equal("clone", ChangeFlow.Names[0]);
        Assert.Equal("branch", ChangeFlow.Names[1]);
        Assert.Equal("synthesize-patch", ChangeFlow.Names[2]);
        Assert.Equal("build", ChangeFlow.Names[3]);
        Assert.Equal("test", ChangeFlow.Names[4]);
        Assert.Equal("diff-review", ChangeFlow.Names[5]);
        Assert.Equal("approval-gate", ChangeFlow.Names[6]);
    }

    [Fact]
    public async Task ChangeFlowExecutesStagesWithTempIsolationAndPausesAtApprovalGate()
    {
        var flow = new ChangeFlow();
        var inner = new ChangeInner
        {
            RunId = "run-test-101",
            Goal = "Update counter logic",
            Subject = "counter.unit",
            BaseFiles = new Dictionary<string, string>
            {
                ["spec.md"] = "# Counter Unit\nOriginal spec.",
                ["Counter.cs"] = "public class Counter { public int Value { get; set; } }"
            },
            PatchFiles = new Dictionary<string, string>
            {
                ["Counter.cs"] = "public class Counter { public int Value { get; set; } public void Increment() => Value++; }"
            }
        };

        // 1. Clone stage: creates isolated temp dir
        var res1 = await flow.ExecuteStep("clone", inner, TestContext.Current.CancellationToken);
        Assert.Null(res1.Wait);
        Assert.True(Directory.Exists(inner.TempDirectory));
        Assert.StartsWith(Path.GetTempPath(), inner.TempDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intochat-cf-run-test-101", inner.TempDirectory, StringComparison.Ordinal);

        // 2. Branch stage: sets target branch
        var res2 = await flow.ExecuteStep("branch", inner, TestContext.Current.CancellationToken);
        Assert.Null(res2.Wait);
        Assert.Equal("change/run-test-101", inner.TargetBranch);

        // 3. SynthesizePatch stage: applies patch to temp dir
        var res3 = await flow.ExecuteStep("synthesize-patch", inner, TestContext.Current.CancellationToken);
        Assert.Null(res3.Wait);
        var counterOnDisk = await File.ReadAllTextAsync(Path.Combine(inner.TempDirectory, "Counter.cs"), TestContext.Current.CancellationToken);
        Assert.Contains("Increment()", counterOnDisk);

        // 4. Build stage: verifies build
        var res4 = await flow.ExecuteStep("build", inner, TestContext.Current.CancellationToken);
        Assert.Null(res4.Wait);
        Assert.True(inner.BuildSuccess);
        Assert.Contains("Build succeeded", inner.BuildSummary);

        // 5. Test stage: runs test checks
        var res5 = await flow.ExecuteStep("test", inner, TestContext.Current.CancellationToken);
        Assert.Null(res5.Wait);
        Assert.True(inner.TestSuccess);
        Assert.Contains("Tests passed", inner.TestSummary);

        // 6. DiffReview stage: computes structured unified diff
        var res6 = await flow.ExecuteStep("diff-review", inner, TestContext.Current.CancellationToken);
        Assert.Null(res6.Wait);
        Assert.NotNull(inner.Diff);
        Assert.True(inner.Diff.TotalAdditions > 0);
        Assert.Contains("+public class Counter", inner.DiffText);

        // 7. ApprovalGate stage: pauses waiting for approval
        var res7 = await flow.ExecuteStep("approval-gate", inner, TestContext.Current.CancellationToken);
        Assert.NotNull(res7.Wait);
        Assert.Equal("approval-gate", res7.Wait.Step);
        Assert.Equal(WaitReason.Approval, res7.Wait.Reason);
        Assert.NotNull(res7.Wait.ConfirmationPlanId);

        // Verify confirmation plan is targeted to creator
        var plan = flow.GetPlanFor("approval-gate", inner);
        Assert.Equal("creator", plan.TargetLabel);
        Assert.Contains("creator", plan.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("counter.unit", plan.Title, StringComparison.OrdinalIgnoreCase);

        // Clean up temp directory
        ChangeFlow.CleanupTempDirectory(inner.TempDirectory);
        Assert.False(Directory.Exists(inner.TempDirectory));
    }

    [Fact]
    public async Task ApprovalPromotesChangesAndCleansUpTempDirectory()
    {
        var flow = new ChangeFlow();
        var inner = new ChangeInner
        {
            RunId = "run-promote-1",
            Goal = "Add helper",
            Subject = "helper.unit",
            BaseFiles = new Dictionary<string, string>
            {
                ["spec.md"] = "# Helper"
            }
        };

        // Run through stages
        await flow.ExecuteStep("clone", inner, TestContext.Current.CancellationToken);
        await flow.ExecuteStep("branch", inner, TestContext.Current.CancellationToken);
        await flow.ExecuteStep("synthesize-patch", inner, TestContext.Current.CancellationToken);
        await flow.ExecuteStep("build", inner, TestContext.Current.CancellationToken);
        await flow.ExecuteStep("test", inner, TestContext.Current.CancellationToken);
        await flow.ExecuteStep("diff-review", inner, TestContext.Current.CancellationToken);

        var tempDir = inner.TempDirectory;
        Assert.True(Directory.Exists(tempDir));

        // Mark as approved and execute approval-gate / promote
        inner.IsApproved = true;
        var res = await flow.ExecuteStep("approval-gate", inner, TestContext.Current.CancellationToken);

        Assert.Null(res.Wait);
        Assert.True(inner.Promoted);
        Assert.NotEmpty(inner.PromotedFiles);

        // Invariant: Temp directory MUST be cleaned up on promote
        Assert.False(Directory.Exists(tempDir));
    }

    [Fact]
    public async Task RejectionAbortsCleanlyAndCleansUpTempDirectory()
    {
        var flow = new ChangeFlow();
        var inner = new ChangeInner
        {
            RunId = "run-reject-1",
            Goal = "Dangerous change",
            Subject = "critical.unit",
            BaseFiles = new Dictionary<string, string>
            {
                ["spec.md"] = "# Critical Unit"
            }
        };

        await flow.ExecuteStep("clone", inner, TestContext.Current.CancellationToken);
        var tempDir = inner.TempDirectory;
        Assert.True(Directory.Exists(tempDir));

        // Mark as aborted
        inner.Aborted = true;
        inner.AbortReason = "Security check failed";

        var ex = await Assert.ThrowsAsync<FlowFailed>(() => flow.ExecuteStep("approval-gate", inner, TestContext.Current.CancellationToken));
        Assert.Equal("approval-gate", ex.Step);
        Assert.Contains("Security check failed", ex.Message);

        // Invariant: Temp directory MUST be cleaned up on abort
        Assert.False(Directory.Exists(tempDir));
    }
}
