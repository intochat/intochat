using DigitalBrain;
using Orleans.Concurrency;

namespace DigitalBrain.Twilio;

// One Twilio account that sends SMS on {brain}/connection/twilio/{id}: a member's pasted credentials,
// or the deployment's own account through the "deployment" method.
[Alias("twilio")]
[Orleans.Metadata.DefaultGrainType("twilio")]
public interface ITwilioSms : INeuron
{
    // A repeated ClientReference returns the earlier message without sending again.
    Task<SmsMessage> Send(SendSms command);

    [ReadOnly]
    Task<SmsMessage[]> ReadRecent(int limit);
}

// To is E.164 (+51987654321); Body is at most 1600 characters, Twilio's limit.
[GenerateSerializer, Alias("twilio.send-sms")]
public sealed record SendSms(
    [property: Id(0)] string To,
    [property: Id(1)] string Body,
    [property: Id(2)] string? ClientReference = null);

// Status is Twilio's (queued, sent, delivered, undelivered, failed); a refused send has no Sid.
[GenerateSerializer, Alias("twilio.sms-message")]
public sealed record SmsMessage(
    [property: Id(0)] string Sid,
    [property: Id(1)] string To,
    [property: Id(2)] string Status,
    [property: Id(3)] DateTimeOffset SentAt,
    [property: Id(4)] string? ClientReference,
    [property: Id(5)] string? ErrorCode);
