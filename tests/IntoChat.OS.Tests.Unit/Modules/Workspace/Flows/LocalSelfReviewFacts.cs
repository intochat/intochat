using IntoChat.OS.Modules.Apps.Flows;
using DigitalBrain;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// The syntax rules run in the Roslyn module; each fact reaches them through IRoslyn on the shared TestOS.
public sealed class LocalSelfReviewFacts : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private readonly string _worktreeRoot;

    private readonly RoslynFixture _fixture;

    public LocalSelfReviewFacts(RoslynFixture fixture)
    {
        _fixture = fixture;
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
    public async Task SwallowedExceptionInChangedFileProducesFindingOnApprovalCard()
    {
        var relativePath = "Sample/Worker.cs";
        var fullPath = Path.Combine(_worktreeRoot, "Sample", "Worker.cs");
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

        var report = await Review(relativePath);

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
    public async Task WallClockAndAmbientLookupProduceFindingsWhileCleanCodeIsEmpty()
    {
        var badRelative = "Sample/Bad.cs";
        var badFull = Path.Combine(_worktreeRoot, "Sample", "Bad.cs");
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

        var report = await Review(badRelative);

        Assert.Contains(report.Findings, f => f.RuleId == "HR-12");
        Assert.Contains(report.Findings, f => f.RuleId == "HR-23");

        var cleanRelative = "Sample/Clean.cs";
        var cleanFull = Path.Combine(_worktreeRoot, "Sample", "Clean.cs");
        File.WriteAllText(cleanFull, """
            namespace Sample;

            public static class Clean
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        var cleanReport = await Review(cleanRelative);
        Assert.Empty(cleanReport.Findings);
        Assert.False(cleanReport.HasBlocking);
        Assert.DoesNotContain("### Self-Review", ChangeCard.Body(CreateChange(cleanRelative), cleanReport), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewUnifiedDiffFlagsSwallowArchitectureBoundaryAndSecretViolations()
    {
        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
        var roslyn = _fixture.Runtime.Get<IRoslyn>(new NeuronId(brain, "microsoft.roslyn", "self-review"));

        var fakeToken = string.Concat("ghp", "_", new string('a', 36));
        var badDiff = $$"""
            diff --git a/src/Sample/Worker.cs b/src/Sample/Worker.cs
            --- a/src/Sample/Worker.cs
            +++ b/src/Sample/Worker.cs
            @@ -10,0 +11,6 @@
            +        try
            +        {
            +            Run();
            +        }
            +        catch (Exception) { }
            +        var token = "{{fakeToken}}";
            diff --git a/framework/src/DigitalBrain/Leak.cs b/framework/src/DigitalBrain/Leak.cs
            --- a/framework/src/DigitalBrain/Leak.cs
            +++ b/framework/src/DigitalBrain/Leak.cs
            @@ -1,0 +1,2 @@
            +using IntoChat.OS.Modules.Apps;
            +public sealed class Leak { }
            diff --git a/src/IntoChat.OS.Host/HostLeak.cs b/src/IntoChat.OS.Host/HostLeak.cs
            --- a/src/IntoChat.OS.Host/HostLeak.cs
            +++ b/src/IntoChat.OS.Host/HostLeak.cs
            @@ -1,0 +1,2 @@
            +using IntoChat.OS.Modules.Workspace;
            +public sealed class HostLeak { }
            """;

        var ct = TestContext.Current.CancellationToken;
        var report = await LocalSelfReview.ReviewUnifiedDiff(roslyn, badDiff, ct);

        Assert.True(report.HasBlocking);
        Assert.Contains(report.Findings, f => f.RuleId == LocalSelfReview.SwallowRuleId && f.Severity == ReviewSeverity.Blocking);
        Assert.Contains(report.Findings, f => f.RuleId == LocalSelfReview.SecretRuleId && f.Severity == ReviewSeverity.Blocking);
        Assert.Contains(report.Findings, f => f.RuleId == LocalSelfReview.FrameworkIsolationRuleId && f.Severity == ReviewSeverity.Blocking);
        Assert.Contains(report.Findings, f => f.RuleId == LocalSelfReview.HostBoundaryRuleId && f.Severity == ReviewSeverity.Blocking);

        // Fallback mode (without IRoslyn) also blocks all three categories deterministically.
        var fallbackReport = await LocalSelfReview.ReviewUnifiedDiffOrFallback(null, badDiff, ct);
        Assert.True(fallbackReport.HasBlocking);
        Assert.Contains(fallbackReport.Findings, f => f.RuleId == LocalSelfReview.SwallowRuleId);
        Assert.Contains(fallbackReport.Findings, f => f.RuleId == LocalSelfReview.SecretRuleId);
        Assert.Contains(fallbackReport.Findings, f => f.RuleId == LocalSelfReview.FrameworkIsolationRuleId);
        Assert.Contains(fallbackReport.Findings, f => f.RuleId == LocalSelfReview.HostBoundaryRuleId);
    }

    [Fact]
    public async Task ReviewUnifiedDiffAcceptsCleanDiffAndPlaceholderSecrets()
    {
        const string cleanDiff = """
            diff --git a/src/Sample/Clean.cs b/src/Sample/Clean.cs
            --- a/src/Sample/Clean.cs
            +++ b/src/Sample/Clean.cs
            @@ -1,0 +1,5 @@
            +public static class Clean
            +{
            +    public const string Sample = "api_key=\"your-placeholder-value\"";
            +    public static int Add(int a, int b) => a + b;
            +}
            """;

        var report = await LocalSelfReview.ReviewUnifiedDiffOrFallback(null, cleanDiff, TestContext.Current.CancellationToken);
        Assert.False(report.HasBlocking);
        Assert.Empty(report.Findings);
    }

    private async Task<SelfReviewReport> Review(params string[] changedFiles)
    {
        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
        var roslyn = _fixture.Runtime.Get<IRoslyn>(new NeuronId(brain, "microsoft.roslyn", "self-review"));
        return await LocalSelfReview.ReviewWorktree(roslyn, _worktreeRoot, changedFiles);
    }

    private static Change CreateChange(string filePath)
    {
        return Change.Start("run-review", RepositoryId.Parse("os"), "Fix worker error handling")
            .WithWorkTree(
                "run-review",
                new WorkTreeLease(
                    "run-review",
                    "os",
                    "/repo/os",
                    "/home/worktrees/os/run-review",
                    "/repo/os/.git/worktrees/run-review",
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
