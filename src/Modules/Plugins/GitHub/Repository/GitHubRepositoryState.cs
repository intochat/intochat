using DigitalBrain.Capabilities.Connections;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

[GenerateSerializer, Alias("github.issue-comment-receipt")]
internal sealed record IssueCommentReceipt(
    [property: Id(0)] int IssueNumber,
    [property: Id(1)] string RequestId,
    [property: Id(2)] long CommentId,
    [property: Id(3)] string CommentUrl,
    [property: Id(4)] string[] AppliedLabels,
    [property: Id(5)] DateTimeOffset CreatedAt);

[GenerateSerializer, Alias("github.repository-state")]
internal sealed record GitHubRepositoryState : ConnectionState
{
    [Id(8)] public long AppId { get; set; }
    [Id(9)] public long InstallationId { get; set; }
    [Id(10)] public long RepositoryId { get; set; }
    [Id(11)] public string Owner { get; set; } = "";
    [Id(12)] public string Name { get; set; } = "";
    [Id(13)] public PullRequestSnapshot[] PullRequests { get; set; } = [];
    [Id(14)] public string[] ProcessedDeliveries { get; set; } = [];
    [Id(15)] public DateTimeOffset? LastWebhookAt { get; set; }
    [Id(16)] public string AuthKind { get; set; } = "app";
    [Id(17)] public IssueSnapshot[] Issues { get; set; } = [];
    [Id(18)] public IssueCommentPreview[] DraftPreviews { get; set; } = [];
    [Id(19)] public IssueCommentReceipt[] ProcessedCommentReceipts { get; set; } = [];
}
