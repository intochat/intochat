# GitHub Issue Triager

Monitors open repository issues on GitHub every 10 minutes, detects high-priority issues (marked 'p0', 'crash', or 'bug'), and publishes triaged status.

- Every `10m`, fetches open issues from GitHub API via `github` slot.
- Computes `OpenCount`, `P0Count`, and saves `LatestTitle`.
- Publishes `issues.triaged(int OpenCount, int P0Count)`.
- Reacts to `triage.reset` by clearing alert flags.
- The view renders KPI summary badges and high-priority alerts.
