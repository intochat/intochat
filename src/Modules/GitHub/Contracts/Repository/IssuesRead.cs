namespace IntoChat.OS.Modules.Microsoft.GitHub;

public sealed record IssuesRead(
    IssueSnapshot[] Snapshots,
    bool Available);
