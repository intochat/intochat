using DigitalBrain.Access;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Mail.Templates;
using IntoChat.OS;

namespace IntoChat.Cloud.Auth.Neurons;

// One email-code slot per (subject, purpose): the neuron turn is the lock (W4 D1). The plain code never leaves
// this neuron in a result (R10); it reaches only the mail sender, off the request path (R3).
[DigitalBrain.Contracts.NeuronAlias("auth.code"), DigitalBrain.Contracts.DefaultNeuronType("auth.code")]
public interface IAuthCode
{
    const string Type = "auth.code";

    static string Key(string subjectKey, CodePurpose purpose) => $"{NeuronKeys.SystemShard(subjectKey)}/auth/code/{subjectKey}/{purpose}";

    [Internal]
    Task<CodeIssue> Issue(CodeIssueRequest request);

    [Internal]
    Task<CodeIssue> Resend();

    [Internal]
    Task<CodeVerdict> Verify(string codeId, string code);

    [Internal]
    Task<AttemptReservation> ReserveAttempt(string codeId);

    [Internal]
    Task<bool> Consume(string codeId);

    [Internal]
    Task<ChallengeInfo?> GetChallenge(string codeId);

    [Internal]
    Task<string?> GetVerifiedChangeSubject(string codeId);

    [Internal]
    Task<string?> GetChangeSubject();

    [Internal]
    Task<TicketIssue?> IssueTicket(string codeId);

    [Internal]
    Task<bool> RedeemTicket(string ticket);
}

public sealed record CodeDelivery(
    string To,
    string Language,
    string UserName,
    MailTemplateId CodeTemplate,
    MailTemplateId? NoticeTemplate);

public sealed record CodeIssueRequest(
    string SubjectHash,
    bool IsDecoy,
    bool EnforceCooldown,
    CodeDelivery? Delivery,
    string[] AllowedMethods,
    string AmrOrigin);

public enum CodeIssueOutcome
{
    Issued,
    RateLimited,
}

public sealed record CodeIssue(
    CodeIssueOutcome Outcome,
    string? CodeId,
    DateTimeOffset? ResendAvailableAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RetryAt);

public enum CodeOutcome
{
    Success,
    Invalid,
    Expired,
    Locked,
    RateLimited,
    NotFound,
}

public sealed record CodeVerdict(
    CodeOutcome Outcome,
    int? AttemptsLeft,
    CodeIssue? Reissued,
    DateTimeOffset? RetryAt,
    string? SubjectHash);

public sealed record AttemptReservation(
    bool Allowed,
    int AttemptsLeft,
    CodeIssue? Reissued);

public sealed record ChallengeInfo(
    string[] AllowedMethods,
    string AmrOrigin,
    string SubjectHash);

// The reset ticket is the one secret returned, by design, to the person who proved the code.
public sealed record TicketIssue(
    string Ticket,
    DateTimeOffset ExpiresAt);
