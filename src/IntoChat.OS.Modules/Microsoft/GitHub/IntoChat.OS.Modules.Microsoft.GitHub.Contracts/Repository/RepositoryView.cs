namespace IntoChat.OS.Microsoft.GitHub;

[GenerateSerializer, Alias("db.github.repository-view")]
public sealed record RepositoryView(
    [property: Id(0)] string ConnectionId,
    [property: Id(1)] bool Connected,
    [property: Id(2)] IReadOnlyList<PullRequestSnapshot> PullRequests,
    [property: Id(3)] DateTimeOffset? LastWebhookAt);
