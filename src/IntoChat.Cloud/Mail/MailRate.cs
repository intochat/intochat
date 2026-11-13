using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Mail;

public sealed class MailRateState
{
    public DateTimeOffset[] SendTimes { get; set; } = [];
}

[Signal("mail.rate-acquired")]
public sealed record MailRateAcquired(string Template) : DigitalBrain.Signal;

[DigitalBrain.Neurons.Neuron(IMailRate.Type)]
internal sealed class MailRateNeuron(TimeProvider clock) : Neuron<MailRateState>, IMailRate
{
    internal const int MaxPerHour = 5;
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public async Task<MailRateDecision> TryAcquire(MailTemplateId template)
    {
        var now = clock.GetUtcNow();
        var recent = State.SendTimes.Where(time => now - time < Window).ToArray();
        if (recent.Length >= MaxPerHour)
        {
            return new MailRateDecision(false, recent.Min() + Window);
        }

        var cooldown = Cooldown(template);
        if (recent.Length > 0 && cooldown > TimeSpan.Zero && now < recent.Max() + cooldown)
        {
            return new MailRateDecision(false, recent.Max() + cooldown);
        }

        await Save(new MailRateState { SendTimes = [.. recent, now] }, new MailRateAcquired(template.ToString()));
        return new MailRateDecision(true, null);
    }

    internal static TimeSpan Cooldown(MailTemplateId template) => template switch
    {
        MailTemplateId.SignupCode or MailTemplateId.LoginCode or MailTemplateId.ResetCode or MailTemplateId.ChangeEmailCode
            => TimeSpan.FromSeconds(42),
        MailTemplateId.TeamInvite => TimeSpan.FromSeconds(60),
        _ => TimeSpan.Zero,
    };
}
