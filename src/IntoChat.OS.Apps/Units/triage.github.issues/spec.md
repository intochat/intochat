# GitHub Issue Triager

Reacts to `github.issue-changed` (`IssueChanged`) signals from connected GitHub repositories and tracks triaged issue counts and attention alerts.

- Reacts to `github.issue-changed` (`IssueChanged`) via `[Synapse(typeof(IssueChanged), "triage.github.issues")]`.
- Tracks `TriagedCount`, `P0Count`, `LastIssueNumber`, `LastAction`, and `LastSubject`.
- Publishes `issues.triaged(int IssueNumber, string Action, int TriagedCount, int P0Count)`.
- Reacts to `triage.reset` by clearing pending attention alerts.
- The view renders KPI summary badges and the latest triaged issue.
