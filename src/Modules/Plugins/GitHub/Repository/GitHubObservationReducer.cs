using System.Globalization;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

internal sealed record IssueReductionResult(
    IssueSnapshot[] Issues,
    IssueChanged[] Signals,
    bool Changed,
    string? FailureReason = null);

internal sealed record PullRequestReductionResult(
    PullRequestSnapshot[] PullRequests,
    PullRequestChanged[] Signals,
    bool Changed,
    string? FailureReason = null);

/// <summary>
/// Pure, deterministic reconciliation, capacity eviction, and signal synthesis
/// for observed GitHub pull request and issue snapshots.
/// </summary>
internal static class GitHubObservationReducer
{
    internal const int MaxPullRequests = 512;
    internal const int MaxIssues = 256;

    public static IssueReductionResult ReconcilePolledIssues(
        IssueSnapshot[] currentIssues,
        IReadOnlyList<IssueSnapshot> polledItems,
        long repositoryId,
        string owner,
        string name,
        int maxIssues = MaxIssues)
    {
        var observedIssues = currentIssues.ToDictionary(static item => item.Number);
        var signals = new List<IssueChanged>();
        var changed = false;

        foreach (var rawIssue in polledItems)
        {
            if (rawIssue.RepositoryId != repositoryId || rawIssue.Number <= 0)
            {
                continue;
            }

            var fencedBody = IssueRead.FenceUntrustedBody(owner, name, repositoryId, rawIssue);
            var fencedIssue = rawIssue with { Body = fencedBody };
            var previous = observedIssues.GetValueOrDefault(fencedIssue.Number);
            if (previous is null && observedIssues.Count >= maxIssues)
            {
                EvictIssue(observedIssues);
            }

            if (previous is null || !string.Equals(previous.Revision, fencedIssue.Revision, StringComparison.Ordinal))
            {
                var action = previous is null ? "opened" : "edited";
                observedIssues[fencedIssue.Number] = fencedIssue;
                changed = true;
                signals.Add(new IssueChanged(
                    fencedIssue.Number,
                    action,
                    fencedIssue.Revision,
                    string.Create(CultureInfo.InvariantCulture, $"github:{repositoryId}:issue:{fencedIssue.Number}")));
            }
        }

        return new IssueReductionResult(
            changed ? observedIssues.Values.OrderBy(static item => item.Number).ToArray() : currentIssues,
            [.. signals],
            changed);
    }

    public static IssueReductionResult ApplyIssueEvent(
        IssueSnapshot[] currentIssues,
        IssueSnapshot issue,
        RepositoryEvent trigger,
        long repositoryId,
        int maxIssues = MaxIssues)
    {
        if (issue.RepositoryId != repositoryId || issue.Number <= 0)
        {
            return new IssueReductionResult(
                currentIssues,
                [],
                Changed: false,
                FailureReason: "Observed evidence belongs to a different repository.");
        }

        var observedIssues = currentIssues.ToDictionary(static item => item.Number);
        if (!observedIssues.ContainsKey(issue.Number) && observedIssues.Count >= maxIssues)
        {
            EvictIssue(observedIssues);
        }

        observedIssues[issue.Number] = issue;
        var action = trigger.Action ?? (trigger.Event == "issue_comment" ? "created" : "updated");
        var version = trigger.Event == "issue_comment" && !string.IsNullOrWhiteSpace(trigger.BodyHash)
            ? $"{issue.Revision}:{trigger.BodyHash[..Math.Min(12, trigger.BodyHash.Length)]}"
            : issue.Revision;
        var signal = new IssueChanged(
            issue.Number,
            action,
            version,
            string.Create(CultureInfo.InvariantCulture, $"github:{repositoryId}:issue:{issue.Number}"));

        return new IssueReductionResult(
            observedIssues.Values.OrderBy(static item => item.Number).ToArray(),
            [signal],
            Changed: true);
    }

    public static PullRequestReductionResult ReconcilePullRequests(
        PullRequestSnapshot[] currentPullRequests,
        IReadOnlyList<PullRequestSnapshot> snapshots,
        long repositoryId,
        RepositoryEvent? trigger = null,
        int maxPullRequests = MaxPullRequests)
    {
        var observed = currentPullRequests.ToDictionary(static item => item.Number);
        var facts = new List<PullRequestChanged>();

        foreach (var snapshot in snapshots)
        {
            if (ApplyPullRequest(observed, snapshot, facts, repositoryId, trigger, maxPullRequests) is { } reason)
            {
                return new PullRequestReductionResult(
                    currentPullRequests,
                    [],
                    Changed: false,
                    FailureReason: reason);
            }
        }

        var changed = facts.Count > 0 || observed.Count != currentPullRequests.Length;
        return new PullRequestReductionResult(
            observed.Values.OrderBy(static item => item.Number).ToArray(),
            [.. facts],
            changed);
    }

    private static void EvictIssue(Dictionary<int, IssueSnapshot> observedIssues)
    {
        var evictionCandidate = observedIssues.Values.Where(static item => !item.IsOpen).OrderBy(static item => item.ObservedAt).FirstOrDefault()
            ?? observedIssues.Values.OrderBy(static item => item.ObservedAt).First();
        observedIssues.Remove(evictionCandidate.Number);
    }

    private static string? ApplyPullRequest(
        Dictionary<int, PullRequestSnapshot> observed,
        PullRequestSnapshot snapshot,
        List<PullRequestChanged> facts,
        long repositoryId,
        RepositoryEvent? trigger,
        int maxPullRequests)
    {
        if (snapshot.RepositoryId != repositoryId || snapshot.Number <= 0)
        {
            return "Observed evidence belongs to a different repository.";
        }

        var previous = observed.GetValueOrDefault(snapshot.Number);
        if (previous is null && observed.Count >= maxPullRequests)
        {
            var expired = observed.Values.Where(static item => !item.IsOpen).OrderBy(static item => item.ObservedAt).FirstOrDefault();
            if (expired is null)
            {
                return "The repository observation capacity is full.";
            }

            observed.Remove(expired.Number);
        }

        observed[snapshot.Number] = snapshot;
        var change = (PullRequestChange)0;
        if (previous is null || !previous.IsOpen && snapshot.IsOpen)
        {
            change |= snapshot.IsOpen ? PullRequestChange.Opened : PullRequestChange.Closed;
        }
        else if (previous.IsOpen && !snapshot.IsOpen)
        {
            change |= PullRequestChange.Closed;
        }
        else if (previous.Revision != snapshot.Revision || previous.IsDraft != snapshot.IsDraft)
        {
            change |= PullRequestChange.Updated;
        }

        if (previous is not null && previous.CiRevision != snapshot.CiRevision)
        {
            change |= PullRequestChange.Checks;
        }

        if (trigger?.Number == snapshot.Number && trigger.Action is "opened" or "reopened" or "closed")
        {
            change |= trigger.Action == "closed" ? PullRequestChange.Closed : PullRequestChange.Opened;
        }
        else if (trigger?.Number == snapshot.Number && trigger.Action == "ready_for_review")
        {
            change |= PullRequestChange.Updated;
        }

        if (change != 0)
        {
            var kind = change.HasFlag(PullRequestChange.Closed)
                ? PullRequestKind.Closed
                : change.HasFlag(PullRequestChange.Opened)
                    ? PullRequestKind.Opened
                    : (trigger?.Number == snapshot.Number && trigger.Action == "ready_for_review")
                        || (previous is not null && previous.IsDraft && !snapshot.IsDraft)
                        ? PullRequestKind.ReadyForReview
                        : change.HasFlag(PullRequestChange.Updated)
                            ? PullRequestKind.Updated
                            : PullRequestKind.ChecksChanged;
            var author = !string.IsNullOrWhiteSpace(snapshot.Author)
                ? snapshot.Author
                : (trigger?.Number == snapshot.Number ? trigger.Author ?? string.Empty : string.Empty);

            facts.Add(new(
                snapshot.Number,
                $"{snapshot.Revision}:{snapshot.CiRevision}",
                $"github:{snapshot.RepositoryId}:{snapshot.Number}",
                Kind: kind,
                HeadSha: snapshot.HeadSha,
                Author: author,
                IsDraft: snapshot.IsDraft));
        }

        return null;
    }
}
