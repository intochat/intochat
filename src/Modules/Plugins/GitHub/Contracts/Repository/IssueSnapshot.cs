namespace IntoChat.OS.Modules.Microsoft.GitHub;

[GenerateSerializer, Alias("github.issue-snapshot")]
public sealed record IssueSnapshot(
    [property: Id(0)] int Number,
    [property: Id(1)] string Title,
    [property: Id(2)] string Body,
    [property: Id(3)] string Url,
    [property: Id(4)] bool IsOpen,
    [property: Id(5)] string Author,
    [property: Id(6)] string[] Labels,
    [property: Id(7)] int CommentCount,
    [property: Id(8)] DateTimeOffset ObservedAt,
    [property: Id(9)] DateTimeOffset CreatedAt,
    [property: Id(10)] DateTimeOffset? UpdatedAt,
    [property: Id(11)] string Revision,
    [property: Id(12)] long RepositoryId = 0);
