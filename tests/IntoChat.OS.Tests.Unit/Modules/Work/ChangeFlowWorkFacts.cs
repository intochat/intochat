using IntoChat.OS.Modules.Work;

namespace IntoChat.OS.Tests.Unit.Modules.Work;

public sealed class ChangeFlowWorkFacts
{
    [Fact]
    public void ChangeStagesDefineSevenPipelineStagesAndPromote()
    {
        var stages = Enum.GetValues<ChangeStage>();
        Assert.Equal(8, stages.Length);
        Assert.Equal(ChangeStage.Clone, stages[0]);
        Assert.Equal(ChangeStage.Branch, stages[1]);
        Assert.Equal(ChangeStage.SynthesizePatch, stages[2]);
        Assert.Equal(ChangeStage.Build, stages[3]);
        Assert.Equal(ChangeStage.Test, stages[4]);
        Assert.Equal(ChangeStage.DiffReview, stages[5]);
        Assert.Equal(ChangeStage.ApprovalGate, stages[6]);
        Assert.Equal(ChangeStage.Promote, stages[7]);
    }

    [Fact]
    public void ChangeWorkspaceEngineEnforcesTempIsolationAndCleansUp()
    {
        string tempPath;
        using (var engine = new ChangeWorkspaceEngine("test-isolation-1"))
        {
            tempPath = engine.TempDirectory;
            Assert.StartsWith(Path.GetTempPath(), tempPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("intochat-cf-test-isolation-1", tempPath, StringComparison.Ordinal);
            Assert.True(Directory.Exists(tempPath));

            var initialFiles = new Dictionary<string, string>
            {
                ["README.md"] = "# Project Title",
                ["code/program.cs"] = "// entry point\nConsole.WriteLine(\"test\");"
            };

            engine.CloneFrom(initialFiles);
            var current = engine.ReadCurrentFiles();
            Assert.Equal(2, current.Count);
            Assert.Equal("# Project Title", current["README.md"]);

            var patch = new Dictionary<string, string>
            {
                ["code/program.cs"] = "// entry point\nConsole.WriteLine(\"updated\");",
                ["code/feature.cs"] = "public class Feature {}"
            };

            engine.ApplyPatch(patch);
            var updated = engine.ReadCurrentFiles();
            Assert.Equal(3, updated.Count);
            Assert.Contains("updated", updated["code/program.cs"]);

            var diff = engine.ComputeDiff(initialFiles);
            Assert.Equal(2, diff.Files.Count);
            Assert.True(diff.TotalAdditions > 0);
        }

        // Disposal MUST have cleaned up the temp directory
        Assert.False(Directory.Exists(tempPath));
    }

    [Fact]
    public void UnifiedDiffGeneratorProducesStructuredDiffAndHunks()
    {
        var baseFiles = new Dictionary<string, string>
        {
            ["file1.txt"] = "line1\nline2\nline3",
            ["file2.txt"] = "to delete"
        };

        var modifiedFiles = new Dictionary<string, string>
        {
            ["file1.txt"] = "line1\nline2 modified\nline3\nline4 added",
            ["file3.txt"] = "new file"
        };

        var diff = UnifiedDiffGenerator.Generate(baseFiles, modifiedFiles);

        Assert.Equal(3, diff.Files.Count);
        Assert.True(diff.TotalAdditions >= 2);
        Assert.True(diff.TotalDeletions >= 1);

        var file1 = Assert.Single(diff.Files, f => f.Path == "file1.txt");
        Assert.Equal(DiffKind.Modified, file1.Kind);
        Assert.NotEmpty(file1.Hunks);

        var file2 = Assert.Single(diff.Files, f => f.Path == "file2.txt");
        Assert.Equal(DiffKind.Deleted, file2.Kind);

        var file3 = Assert.Single(diff.Files, f => f.Path == "file3.txt");
        Assert.Equal(DiffKind.Added, file3.Kind);

        Assert.Contains("--- a/file1.txt", diff.RawText);
        Assert.Contains("+++ b/file1.txt", diff.RawText);
    }

    [Fact]
    public async Task WorkOrchestratorTracksChangeRunsAndTransitions()
    {
        var orchestrator = new WorkOrchestratorNeuron();
        DigitalBrain.Neurons.NeuronAttachment.Attach(
            orchestrator,
            new DigitalBrain.Testing.FakeNeuronContext(DigitalBrain.NeuronId.Parse("facts/work.orchestrator/main")));
        var run = new ChangeRun(
            RunId: "run-001",
            Goal: "Add telemetry to logger",
            Subject: "logger.unit",
            Stage: ChangeStage.Clone,
            Outcome: WorkStageOutcome.InProgress,
            TargetBranch: "change/run-001",
            TempDirectory: Path.Combine(Path.GetTempPath(), "intochat-cf-run-001"),
            ConfirmationPlanId: "plan-logger-1");

        var ct = TestContext.Current.CancellationToken;
        await orchestrator.RegisterChangeRun(run, ct);

        var fetched = await orchestrator.GetChangeRun("run-001", ct);
        Assert.NotNull(fetched);
        Assert.Equal("Add telemetry to logger", fetched.Goal);
        Assert.Equal(ChangeStage.Clone, fetched.Stage);

        await orchestrator.TransitionChangeStage("run-001", ChangeStage.Clone, ChangeStage.Branch, WorkStageOutcome.InProgress);
        var transitioned = await orchestrator.GetChangeRun("run-001", ct);
        Assert.Equal(ChangeStage.Branch, transitioned!.Stage);

        await orchestrator.ResolveApproval("plan-logger-1", ApprovalStatus.Approved, "reviewer");
        var approved = await orchestrator.GetChangeRun("run-001", ct);
        Assert.True(approved!.IsApproved);
        Assert.Equal(ChangeStage.Promote, approved.Stage);
        Assert.Equal(WorkStageOutcome.Succeeded, approved.Outcome);
    }
}
