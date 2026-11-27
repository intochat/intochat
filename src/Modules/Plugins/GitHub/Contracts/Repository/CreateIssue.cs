namespace IntoChat.OS.Modules.Microsoft.GitHub;

[GenerateSerializer, Alias("github.create-issue-request")]
public sealed record CreateIssueRequest(
    [property: Id(0)] string Title,
    [property: Id(1)] string? Body = null,
    [property: Id(2)] string[]? Labels = null,
    [property: Id(3)] string[]? Assignees = null);

[GenerateSerializer, Alias("github.issue-created")]
public sealed record IssueCreated(
    [property: Id(0)] int Number,
    [property: Id(1)] string HtmlUrl,
    [property: Id(2)] string Title);
