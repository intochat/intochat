namespace IntoChat.OS.Modules.Microsoft.GitHub;

[GenerateSerializer, Alias("github.preview-issue-comment")]
public sealed record PreviewIssueCommentRequest(
    [property: Id(0)] int IssueNumber,
    [property: Id(1)] string Body,
    [property: Id(2)] string[]? SuggestedLabels = null);

[GenerateSerializer, Alias("github.issue-comment-preview")]
public sealed record IssueCommentPreview(
    [property: Id(0)] string PreviewId,
    [property: Id(1)] int IssueNumber,
    [property: Id(2)] string Body,
    [property: Id(3)] string[] SuggestedLabels,
    [property: Id(4)] DateTimeOffset ExpiresAt,
    [property: Id(5)] string? Repository = null,
    [property: Id(6)] DateTimeOffset? CreatedAt = null);

[GenerateSerializer, Alias("github.comment-on-issue")]
public sealed record CommentOnIssueRequest(
    [property: Id(0)] int IssueNumber = 0,
    [property: Id(1)] string? Body = null,
    [property: Id(2)] string[]? Labels = null,
    [property: Id(3)] string? PreviewId = null,
    [property: Id(4)] string? RequestId = null);

[GenerateSerializer, Alias("github.issue-comment-result")]
public sealed record IssueCommentResult(
    [property: Id(0)] int IssueNumber,
    [property: Id(1)] long CommentId,
    [property: Id(2)] string CommentUrl,
    [property: Id(3)] string[] AppliedLabels,
    [property: Id(4)] string RequestId,
    [property: Id(5)] bool Deduplicated = false);
