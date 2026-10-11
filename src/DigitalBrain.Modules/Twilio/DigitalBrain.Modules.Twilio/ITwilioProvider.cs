namespace DigitalBrain.Twilio;

internal enum TwilioOutcome { Accepted, Refused, Unauthorized, Unavailable }

// Status and ErrorCode are Twilio's own; Detail is a fixed sentence plus them, never a secret.
internal sealed record TwilioSendResult(TwilioOutcome Outcome, string? Sid, string Status, string? ErrorCode, string Detail);

internal interface ITwilioProvider
{
    Task<TwilioSendResult> SendAsync(TwilioAccount account, string to, string body, Uri? statusCallback, CancellationToken cancellationToken);

    Task<TwilioOutcome> ProbeAsync(TwilioAccount account, CancellationToken cancellationToken);
}
