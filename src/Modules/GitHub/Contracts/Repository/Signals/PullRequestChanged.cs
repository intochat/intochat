
namespace IntoChat.OS.Modules.Microsoft.GitHub.Signals;

public enum PullRequestKind
{
    Opened,
    Updated,
    ReadyForReview,
    ChecksChanged,
    Closed,
}

[DigitalBrain.Signal("github.pull-request-changed")]
public sealed record PullRequestChanged(
    int Number,
    string Version,
    string SubjectKey,
    string? CiStatus = null,
    string? CommentAuthor = null,
    string? CommentBody = null,
    PullRequestKind Kind = PullRequestKind.Updated,
    string HeadSha = "",
    string Author = "",
    bool IsDraft = false) : DigitalBrain.Signal;

