using DigitalBrain;

namespace DigitalBrain.Twilio.Signals;

[GenerateSerializer, Alias("twilio.sms-sent")]
public sealed record SmsSent([property: Id(0)] SmsMessage Message) : Signal;

// A status callback Twilio signed: delivered, sent, undelivered or failed, once per (Sid, Status).
[GenerateSerializer, Alias("twilio.sms-delivered")]
public sealed record SmsDelivered([property: Id(0)] string Sid, [property: Id(1)] string Status) : Signal;

// Detail is the provider's status and error code; never a secret, never the body.
[GenerateSerializer, Alias("twilio.sms-failed")]
public sealed record SmsFailed([property: Id(0)] string To, [property: Id(1)] string Detail) : Signal;
