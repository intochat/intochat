using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Mail;

public interface IMailSender
{
    Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct);
}

public sealed record MailMessage(
    MailTemplateId Template,
    string To,
    string Language,
    IReadOnlyDictionary<string, string> Data);

public sealed record MailSendResult(MailSendOutcome Outcome, string? OperationId, DateTimeOffset? RetryAt);

public enum MailSendOutcome
{
    Accepted,
    RateLimited,
    Failed,
    NotConfigured,
}
