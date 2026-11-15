using System.Globalization;
using System.Text;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Apps.Flows;

// The text of one Feature to PR change, drawn from the run's state alone: the approval's body and what Ino reports of it.
// The same state draws the same text, and nothing here throws: the kernel asks for the body outside any step.
internal static class ChangeCard
{
    // The whole body, the model's summary included. One plan stays under the kernel's 1 MiB call argument and 2 MiB view
    // even when every character costs six bytes of JSON.
    public const int MaxChars = 60_000;

    public const string GoalLabel = "The goal as Ino passed it, written by the model. It becomes the commit subject and the pull request's title and text:";

    public const string SummaryLabel = "Ino's summary, written by the model:";

    public const string DiffLabel = "Diff:";

    // Repository, remote, base, branch, the commit, every file and the line counts. No model text and no diff.
    public static string Header(FeatureToPrInner inner)
    {
        var lease = inner.Lease;
        var files = inner.ChangedFiles ?? [];
        var remote = WorkTreeService.WithoutCredentials(lease?.OriginUrl)?.Replace("\n", ", ", StringComparison.Ordinal);
        var text = new StringBuilder();
        // The lease's id is the registered one; the run's own repository is whatever the caller typed.
        text.Append("Repository: ").Append(lease?.GitHubRepo ?? lease?.RepositoryId ?? inner.Repository).Append('\n');
        text.Append("Remote: ").Append(remote ?? "origin (its address was not recorded)").Append('\n');
        text.Append("Base: ").Append(lease?.BaseBranch ?? "unknown").Append(" at ").Append(lease?.BaseCommitSha ?? "unknown").Append('\n');
        text.Append("Branch: ").Append(lease?.BranchName ?? "unknown").Append('\n');
        text.Append("Commit: ").Append(inner.CommitSha ?? "none").Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Files ({files.Length}):\n");
        foreach (var file in files)
        {
            text.Append("  ").Append(file).Append('\n');
        }

        text.Append(CultureInfo.InvariantCulture, $"Lines: +{inner.Additions} / -{inner.Deletions}");
        return text.ToString();
    }

    public static string Body(FeatureToPrInner inner) => Draw(inner, inner.HostRenderedDiff);

    // Why this change cannot go on an approval, or null when the card shows all of it. The diff is passed apart from the
    // state so a step can ask before it stores the diff.
    public static string? Refusal(FeatureToPrInner inner, string? diff)
    {
        if (FileNotShownAsText(diff) is { } file)
        {
            return $"The change has a file whose content cannot be shown as text: {file}. Nothing was pushed.";
        }

        var size = Draw(inner, diff).Length;
        return size <= MaxChars
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The change is too large to show on one approval card: {size} characters, the limit is {MaxChars}. Nothing was pushed. Ask for a smaller change.");
    }

    private static string Draw(FeatureToPrInner inner, string? diff)
    {
        var text = new StringBuilder();
        // The first line: Ino's dock shows this body under the run's title, not under the plan's.
        text.Append(inner.Lease?.GitHubRepo is { Length: > 0 }
            ? "Approving pushes one commit to a new branch on the remote and opens a draft pull request that names you as its approver."
            : "Approving pushes one commit to a new branch on the remote. It opens no pull request: the remote is not on GitHub.");
        text.Append("\n\n").Append(Header(inner));
        Quote(text, GoalLabel, inner.Goal);
        Quote(text, SummaryLabel, inner.CoderSummary);

        if (!string.IsNullOrWhiteSpace(diff))
        {
            text.Append("\n\n").Append(DiffLabel).Append('\n').Append(diff.TrimEnd());
        }

        return text.ToString();
    }

    // The model's words under a label, every line quoted, so none of them can pass for a line of the host's.
    // A line ends at every character a screen may break at, not only at '\n'.
    private static void Quote(StringBuilder text, string label, string? words)
    {
        if (string.IsNullOrWhiteSpace(words))
        {
            return;
        }

        text.Append("\n\n").Append(label).Append("\n> ");
        foreach (var c in words.Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if ((char.IsControl(c) && c != '\t') || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                text.Append("\n> ");
            }
            else
            {
                text.Append(c);
            }
        }
    }

    // Git writes "Binary files a/x and b/x differ" in place of a file it does not treat as text. Every line inside a hunk
    // starts with '+', '-' or a space, so a line that starts with these words is never file content.
    private static string? FileNotShownAsText(string? diff)
    {
        const string Start = "Binary files ";
        const string End = " differ";
        foreach (var raw in (diff ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < Start.Length + End.Length
                || !line.StartsWith(Start, StringComparison.Ordinal)
                || !line.EndsWith(End, StringComparison.Ordinal))
            {
                continue;
            }

            var names = line[Start.Length..^End.Length];
            var and = names.LastIndexOf(" and ", StringComparison.Ordinal);
            var name = and < 0 ? names : names[(and + " and ".Length)..];
            if (and >= 0 && name == "/dev/null") { name = names[..and]; }
            return name.StartsWith("a/", StringComparison.Ordinal) || name.StartsWith("b/", StringComparison.Ordinal) ? name[2..] : name;
        }

        return null;
    }
}
