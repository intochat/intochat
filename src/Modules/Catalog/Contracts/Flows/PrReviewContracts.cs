using DigitalBrain;

namespace IntoChat.OS.Modules.Apps.Flows;

[Signal("review.pr.requested")]
public sealed record ReviewRequested(string Repository, int Number, string HeadSha) : Signal;

public sealed record SeenPrHeads(string[] Keys)
{
    public const string StateAlias = "review.pr.seen";

    public SeenPrHeads() : this([]) { }
}

public enum PrReviewVerdict
{
    Comment,
    RequestChanges,
    Approve,
}

public sealed record PrReviewState(
    string Repository,
    int Number,
    string HeadSha,
    ReviewFinding[] Findings,
    PrReviewVerdict Verdict,
    string? SubmittedReviewId,
    bool AwaitingApproval,
    string? ConfirmationPlanId = null,
    string? RepositoryNeuronKey = null,
    ReviewFinding[]? ModelFindings = null)
{
    public PrReviewState() : this(string.Empty, 0, string.Empty, [], PrReviewVerdict.Comment, null, false) { }
}
