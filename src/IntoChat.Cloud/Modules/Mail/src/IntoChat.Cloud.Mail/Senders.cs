using Azure;
using Azure.Communication.Email;
using DigitalBrain.Spi;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Mail;

internal interface IAcsEmailTransport
{
    Task<string> SendAsync(string connectionString, EmailMessage message, CancellationToken ct);
}

internal sealed class AcsEmailTransport : IAcsEmailTransport
{
    private readonly object gate = new();
    private EmailClient? client;
    private string? clientConnectionString;

    public async Task<string> SendAsync(string connectionString, EmailMessage message, CancellationToken ct)
    {
        var operation = await Client(connectionString).SendAsync(WaitUntil.Started, message, ct);
        return operation.Id;
    }

    private EmailClient Client(string connectionString)
    {
        lock (gate)
        {
            if (client is null || clientConnectionString != connectionString)
            {
                client = new EmailClient(connectionString);
                clientConnectionString = connectionString;
            }

            return client;
        }
    }
}

internal sealed class AcsMailSender(
    MailTemplateRenderer renderer,
    MailModuleOptions options,
    MailSecrets secrets,
    IAcsEmailTransport transport,
    ILogger<AcsMailSender> logger) : IMailSender
{
    public async Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SenderAddress))
        {
            return new MailSendResult(MailSendOutcome.NotConfigured, null, null);
        }

        var connectionString = await secrets.Named(MailSecrets.AcsConnection);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("Mail {Template} not sent: ACS is not configured", message.Template);
            return new MailSendResult(MailSendOutcome.NotConfigured, null, null);
        }

        var rendered = renderer.Render(message.Template, message.Language, message.Data);
        var content = new EmailContent(rendered.Subject) { Html = rendered.Html, PlainText = rendered.PlainText };
        var email = new EmailMessage(options.SenderAddress, new EmailRecipients([new EmailAddress(message.To)]), content);
        try
        {
            var operationId = await transport.SendAsync(connectionString, email, ct);
            return new MailSendResult(MailSendOutcome.Accepted, operationId, null);
        }
        catch (RequestFailedException failure)
        {
            logger.LogError(
                "ACS mail {Template} failed: status {Status}, error {ErrorCode}, recipient {Recipient}",
                message.Template,
                failure.Status,
                failure.ErrorCode,
                EmailMask.Mask(message.To));
            return new MailSendResult(MailSendOutcome.Failed, null, null);
        }
    }
}

internal sealed class LogMailSender(MailTemplateRenderer renderer, ILogger<LogMailSender> logger) : IMailSender
{
    public Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct)
    {
        var rendered = renderer.Render(message.Template, message.Language, message.Data);
        var body = MailLogRedaction.RedactBody(rendered.PlainText, message.Data);
        logger.LogInformation(
            "Mail {Template} to {Recipient} in {Language}:{NewLine}{Body}",
            message.Template,
            EmailMask.Mask(message.To),
            message.Language,
            Environment.NewLine,
            body);
        return Task.FromResult(new MailSendResult(MailSendOutcome.Accepted, "log", null));
    }
}

internal sealed class RateLimitedMailSender(IMailSender inner, IRecipientHasher hasher, INeuronProxies neurons) : IMailSender
{
    public async Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct)
    {
        var recipientHash = hasher.Hash("mail-rate", message.To);
        var decision = await neurons.Get<IMailRate>(MailIds.Of(IMailRate.Type, IMailRate.Key(recipientHash, message.Template))).TryAcquire(message.Template);
        return decision.Allowed
            ? await inner.SendAsync(message, ct)
            : new MailSendResult(MailSendOutcome.RateLimited, null, decision.RetryAt);
    }
}
