using IntoChat.Cloud.Mail;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

public sealed record SentMail(MailTemplateId Template, string To, string Language, IReadOnlyDictionary<string, string> Data);

// Records every mail the Auth module sends; stands in for the Mail module's sender (09-06).
public sealed class FakeMailSender : IMailSender
{
    private readonly object gate = new();
    private readonly List<SentMail> sent = [];

    public IReadOnlyList<SentMail> Sent
    {
        get
        {
            lock (gate) { return sent.ToArray(); }
        }
    }

    public Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct)
    {
        lock (gate) { sent.Add(new SentMail(message.Template, message.To, message.Language, message.Data)); }
        return Task.FromResult(new MailSendResult(MailSendOutcome.Accepted, Guid.NewGuid().ToString("N"), null));
    }

    public void Clear()
    {
        lock (gate) { sent.Clear(); }
    }
}
