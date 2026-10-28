using IntoChat.Cloud.Mail;
using IntoChat.Cloud.Mail.Templates;
using IntoChat.Cloud.Auth.Neurons;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Auth.Services;

// Sends code and notice mail for the code neuron, off the request path (R3). Every mail outcome is absorbed here: the HTTP
// response never depends on it (R4); failures are logged masked and audited without the address.
internal sealed class CodeMailer(IMailSender sender, AuthModuleOptions options, AuthAudit audit, ILogger<CodeMailer> logger)
{
    // A null code builds the delivery's notice (AccountAlreadyExists); otherwise its code template.
    internal MailMessage BuildMessage(CodeDelivery delivery, string? code)
    {
        var template = code is null
            ? delivery.NoticeTemplate ?? throw new ArgumentException("A notice needs a notice template.", nameof(delivery))
            : delivery.CodeTemplate;
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MailDataKeys.ActionUrl] = ActionUrl(),
            [MailDataKeys.UserName] = delivery.UserName,
        };
        if (code is not null)
        {
            data[MailDataKeys.Code] = code;
        }

        return new MailMessage(template, delivery.To, delivery.Language, data);
    }

    // The one-time welcome notice; its audit and log lines are the notice ones.
    public Task SendWelcomeAsync(string email, string language, string displayName) =>
        SendAsync(new CodeDelivery(email, language, displayName, MailTemplateId.Welcome, MailTemplateId.Welcome), null);

    public async Task SendAsync(CodeDelivery delivery, string? code)
    {
        MailSendResult result;
        try
        {
            result = await sender.SendAsync(BuildMessage(delivery, code), CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            result = new MailSendResult(MailSendOutcome.Failed, null, null);
            logger.LogWarning("Auth mail to {Recipient} threw {Error}", EmailMask.Mask(delivery.To), error.GetType().Name);
        }

        var kind = result.Outcome switch
        {
            MailSendOutcome.RateLimited => AuthAuditKind.MailRateLimited,
            MailSendOutcome.Failed => AuthAuditKind.MailFailed,
            MailSendOutcome.NotConfigured => AuthAuditKind.MailNotConfigured,
            _ => (AuthAuditKind?)null,
        };
        if (kind is { } failed)
        {
            logger.LogWarning("Auth mail to {Recipient} ended {Outcome}", EmailMask.Mask(delivery.To), result.Outcome);
            await audit.RecordAsync(null, failed, result.Outcome.ToString(), null, code is null ? "notice" : "code");
        }
    }

    private string ActionUrl() => (options.PublicBaseUrl ?? "http://localhost").TrimEnd('/') + "/auth";
}
