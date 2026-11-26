namespace IntoChat.OS.Modules.Microsoft.GitHub;

public sealed record InlineComment(string Path, int Line, string Body);

public enum ReviewVerdict
{
    Comment,
    RequestChanges,
    Approve,
}

public sealed record SubmitReviewRequest(
    int Number,
    string HeadSha,
    ReviewVerdict Verdict,
    string Body,
    InlineComment[] Comments);

public sealed record ReviewSubmitted(
    string ReviewId,
    int Number,
    string HeadSha,
    ReviewVerdict Verdict);
