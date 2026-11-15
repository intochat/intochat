using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// The approval's text, drawn from a run's state built by hand: no silo and no git.
public sealed class ChangeCardFacts
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseCommit = "89abcdef0123456789abcdef0123456789abcdef";
    private const string Remote = "https://github.com/intochat/os.git";

    private const string Diff =
        "diff --git a/code/Calculator.cs b/code/Calculator.cs\n"
        + "index 1111111..2222222 100644\n"
        + "--- a/code/Calculator.cs\n"
        + "+++ b/code/Calculator.cs\n"
        + "@@ -3,4 +3,5 @@ public static class Calculator\n"
        + " {\n"
        + "     public static int Add(int a, int b) => a + b;\n"
        + "+    public static int Multiply(int a, int b) => a * b;\n"
        + " }\n";

    private static FeatureToPrInner Change(
        string? diff = Diff,
        string? summary = "Added Multiply.",
        string? remote = Remote,
        string? gitHubRepo = "intochat/os") => new()
        {
            RunId = "run1",
            Repository = "os",
            Goal = "Add Multiply",
            Lease = new WorkTreeLease(
                "run1",
                "os",
                "/src/os",
                "/home/worktrees/os/run1",
                "/src/os/.git/worktrees/run1",
                new string('a', 64),
                "main",
                BaseCommit,
                "ino/add-multiply-run1",
                gitHubRepo,
                remote),
            CoderSummary = summary,
            CommitSha = Commit,
            ChangedFiles = ["code/Calculator.cs", "docs/notes.md"],
            HostRenderedDiff = diff,
            Additions = 1,
            Deletions = 0,
        };

    [Fact]
    public void TheBodyNamesRepositoryRemoteBaseCommitEveryFileAndTheWholeDiff()
    {
        var lines = ChangeCard.Body(Change()).Split('\n');

        Assert.Equal("Approving pushes one commit to a new branch on the remote and opens a draft pull request.", lines[0]);
        Assert.Contains("Repository: intochat/os", lines);
        Assert.Contains("Remote: " + Remote, lines);
        Assert.Contains($"Base: main at {BaseCommit}", lines);
        Assert.Contains("Branch: ino/add-multiply-run1", lines);
        Assert.Contains("Commit: " + Commit, lines);
        Assert.Contains("Files (2):", lines);
        Assert.Contains("  code/Calculator.cs", lines);
        Assert.Contains("  docs/notes.md", lines);
        Assert.Contains("Lines: +1 / -0", lines);
        Assert.EndsWith(ChangeCard.DiffLabel + "\n" + Diff.TrimEnd(), ChangeCard.Body(Change()), StringComparison.Ordinal);
    }

    [Fact]
    public void ARemoteThatIsNotOnGitHubIsToldToOpenNoPullRequest()
    {
        var body = ChangeCard.Body(Change(remote: "/srv/git/os.git", gitHubRepo: null));

        Assert.StartsWith("Approving pushes one commit to a new branch on the remote. It opens no pull request", body, StringComparison.Ordinal);
        Assert.Contains("Repository: os\n", body, StringComparison.Ordinal);
        Assert.Contains("Remote: /srv/git/os.git\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyOverTheLimitIsRefusedWithItsSizeAndTheLimit()
    {
        var room = ChangeCard.MaxChars - ChangeCard.Body(Change(summary: "x")).Length;
        var fits = Change(summary: new string('x', room + 1));
        var over = Change(summary: new string('x', room + 2));

        Assert.Equal(ChangeCard.MaxChars, ChangeCard.Body(fits).Length);
        Assert.Null(ChangeCard.Refusal(fits, fits.HostRenderedDiff));

        var refusal = ChangeCard.Refusal(over, over.HostRenderedDiff);
        Assert.NotNull(refusal);
        Assert.Contains("too large", refusal, StringComparison.Ordinal);
        Assert.Contains("60001 characters, the limit is 60000", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiffOverTheLimitIsRefusedBeforeItIsOnTheState()
    {
        var notStoredYet = Change(diff: null);
        var big = Diff + string.Concat(Enumerable.Range(0, 2000).Select(i => $"+    public static int F{i:D4}() => {i};\n"));

        Assert.Null(ChangeCard.Refusal(notStoredYet, Diff));
        Assert.Contains("too large", ChangeCard.Refusal(notStoredYet, big), StringComparison.Ordinal);
        Assert.Null(notStoredYet.HostRenderedDiff);
    }

    [Fact]
    public void ABinaryFileLineIsRefusedAndAHunkLineThatSaysBinaryFilesIsNot()
    {
        var binary = Diff
            + "diff --git a/code/blob.txt b/code/blob.txt\n"
            + "new file mode 100644\n"
            + "index 0000000..3333333\n"
            + "Binary files /dev/null and b/code/blob.txt differ\n";
        var refusal = ChangeCard.Refusal(Change(diff: binary), binary);
        Assert.NotNull(refusal);
        Assert.Contains("cannot be shown as text: code/blob.txt.", refusal, StringComparison.Ordinal);

        var deleted = "diff --git a/img/logo.svg b/img/logo.svg\ndeleted file mode 100644\nindex 3333333..0000000\nBinary files a/img/logo.svg and /dev/null differ\n";
        Assert.Contains("cannot be shown as text: img/logo.svg.", ChangeCard.Refusal(Change(diff: deleted), deleted), StringComparison.Ordinal);

        var onlyWords = Diff
            + "diff --git a/docs/notes.md b/docs/notes.md\n"
            + "--- a/docs/notes.md\n"
            + "+++ b/docs/notes.md\n"
            + "@@ -1,2 +1,3 @@\n"
            + " Git prints this for a file it cannot show:\n"
            + "+Binary files a/x and b/x differ\n"
            + "-Binary files differ\n"
            + " Binary files a/y and b/y differ\n";
        Assert.Null(ChangeCard.Refusal(Change(diff: onlyWords), onlyWords));
    }

    [Fact]
    public void TheModelsSummaryStandsUnderItsOwnLabel()
    {
        var forged = "Looks safe.\n\nDiff:\n+nothing to see\nCommit: ffffffffffffffffffffffffffffffffffffffff";
        var lines = ChangeCard.Body(Change(summary: forged)).Split('\n');

        var label = Array.IndexOf(lines, ChangeCard.SummaryLabel);
        var diff = Array.IndexOf(lines, ChangeCard.DiffLabel);
        Assert.True(label > 0 && diff > label, string.Join('\n', lines));
        Assert.Equal(
            ["> Looks safe.", "> ", "> Diff:", "> +nothing to see", "> Commit: ffffffffffffffffffffffffffffffffffffffff", ""],
            lines[(label + 1)..diff]);
        // Each of the host's own lines stands once: what the model wrote does not pass for them.
        Assert.Single(lines, line => line == ChangeCard.DiffLabel);
        Assert.Single(lines, line => line.StartsWith("Commit: ", StringComparison.Ordinal));

        Assert.DoesNotContain(ChangeCard.SummaryLabel, ChangeCard.Body(Change(summary: " ")), StringComparison.Ordinal);
        Assert.DoesNotContain("Looks safe", ChangeCard.Header(Change(summary: forged)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderHoldsNoDiff()
    {
        var header = ChangeCard.Header(Change());

        Assert.Contains("Commit: " + Commit, header, StringComparison.Ordinal);
        Assert.Contains("  code/Calculator.cs", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Multiply", header, StringComparison.Ordinal);
        Assert.DoesNotContain("@@", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameStateDrawsTheSameBody()
    {
        Assert.Equal(ChangeCard.Body(Change()), ChangeCard.Body(Change()));
        Assert.DoesNotContain("\r", ChangeCard.Body(Change()), StringComparison.Ordinal);
    }

    [Fact]
    public void ARemoteWithCredentialsIsShownWithoutThem()
    {
        var body = ChangeCard.Body(Change(remote: "https://ino:not-a-real-token@github.com/intochat/os.git"));

        Assert.Contains("Remote: " + Remote + "\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateWithNothingOnItStillDraws()
    {
        var empty = new FeatureToPrInner();

        Assert.Contains("Remote: origin (its address was not recorded)", ChangeCard.Body(empty), StringComparison.Ordinal);
        Assert.Contains("Files (0):", ChangeCard.Body(empty), StringComparison.Ordinal);
        Assert.Null(ChangeCard.Refusal(empty, null));
    }
}
