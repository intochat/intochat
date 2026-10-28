using IntoChat.Cloud.Mail;


namespace IntoChat.Cloud.Auth.Tests;

// FakeMailSender with a settable delay and a forced outcome, so facts prove mail never shapes or slows a response.
public sealed class ControllableMailSender(FakeMailSender inner) : IMailSender
{
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public MailSendOutcome? Outcome { get; set; }

    public async Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, CancellationToken.None);
        }

        return Outcome is { } forced and not MailSendOutcome.Accepted
            ? new MailSendResult(forced, null, null)
            : await inner.SendAsync(message, ct);
    }

    public void Reset()
    {
        Delay = TimeSpan.Zero;
        Outcome = null;
    }
}
