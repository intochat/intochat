using DigitalBrain.Access;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Mail;

[DigitalBrain.Contracts.NeuronAlias("mail.rate"), DigitalBrain.Contracts.DefaultNeuronType("mail.rate")]
public interface IMailRate
{
    const string Type = "mail.rate";

    static string Key(string recipientHash, MailTemplateId template) =>
        $"{SystemShard.BrainOf(recipientHash)}/mail-rate/{recipientHash}/{template}";

    [Internal]
    Task<MailRateDecision> TryAcquire(MailTemplateId template);
}

public sealed record MailRateDecision(
    bool Allowed,
    DateTimeOffset? RetryAt);
