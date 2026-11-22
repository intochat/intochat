using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class LocalSelfReviewFacts : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private readonly string _worktreeRoot;

    public LocalSelfReviewFacts()
    {
        _worktreeRoot = Path.Combine(Path.GetTempPath(), "intochat-self-review-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_worktreeRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_worktreeRoot))
        {
            Directory.Delete(_worktreeRoot, recursive: true);
        }
    }

    [Fact]
    public void SwallowedExceptionInChangedFileProducesFindingOnApprovalCard()
    {
        var relativePath = "src/Sample/Worker.cs";
        var fullPath = Path.Combine(_worktreeRoot, "src", "Sample", "Worker.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, """
            using System;

            namespace Sample;

            public sealed class Worker
            {
                public void Run()
                {
                    try
                    {
                        DoWork();
                    }
                    catch (Exception) { }
                }

                private static void DoWork() { }
            }
            """);

        var report = LocalSelfReview.ReviewWorktree(_worktreeRoot, [relativePath]);

        Assert.True(report.HasBlocking);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("HR-Swallow", finding.RuleId);
        Assert.Equal(ReviewSeverity.Blocking, finding.Severity);
        Assert.Equal(relativePath, finding.Path);
        Assert.Contains("DB0022", finding.Message, StringComparison.Ordinal);

        var change = CreateChange(relativePath);
        var card = ChangeCard.Body(change, report);

        Assert.Contains("### Self-Review", card, StringComparison.Ordinal);
        Assert.Contains("HR-Swallow", card, StringComparison.Ordinal);
        Assert.Contains(relativePath, card, StringComparison.Ordinal);
    }

    [Fact]
    public void WallClockAndAmbientLookupProduceFindingsWhileCleanCodeIsEmpty()
    {
        var badRelative = "src/Sample/Bad.cs";
        var badFull = Path.Combine(_worktreeRoot, "src", "Sample", "Bad.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(badFull)!);
        File.WriteAllText(badFull, """
            using System;

            namespace Sample;

            public sealed class Bad
            {
                public DateTime Now() => DateTime.Now;
                public object Lookup() => SiloNeurons.Get<object>("id");
            }
            """);

        var report = LocalSelfReview.ReviewWorktree(_worktreeRoot, [badRelative]);

        Assert.Contains(report.Findings, f => f.RuleId == "HR-12");
        Assert.Contains(report.Findings, f => f.RuleId == "HR-23");

        var cleanRelative = "src/Sample/Clean.cs";
        var cleanFull = Path.Combine(_worktreeRoot, "src", "Sample", "Clean.cs");
        File.WriteAllText(cleanFull, """
            namespace Sample;

            public static class Clean
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        var cleanReport = LocalSelfReview.ReviewWorktree(_worktreeRoot, [cleanRelative]);
        Assert.Empty(cleanReport.Findings);
        Assert.False(cleanReport.HasBlocking);
        Assert.DoesNotContain("### Self-Review", ChangeCard.Body(CreateChange(cleanRelative), cleanReport), StringComparison.Ordinal);
    }

    private static Change CreateChange(string filePath)
    {
        return Change.Start("run-review", RepositoryId.Parse("os"), "Fix worker error handling")
            .WithWorkTree(
                "run-review",
                new WorkTreeLease(
                    "run-review",
                    "os",
                    "/src/os",
                    "/home/worktrees/os/run-review",
                    "/src/os/.git/worktrees/run-review",
                    new string('a', 64),
                    "main",
                    BaseCommit,
                    "ino/fix-worker-run-review",
                    "intochat/os",
                    "https://github.com/intochat/os.git"))
            .WithCoderEdit(new CoderEditResult("Updated Worker.", [filePath], "test-model"))
            .WithCommit(ChangeSet.Create(
                CommitSha.Parse(Commit),
                [RepoPath.Parse(filePath)],
                $"diff --git a/{filePath} b/{filePath}\n+catch (Exception) {{ }}\n",
                1,
                0));
    }
}
