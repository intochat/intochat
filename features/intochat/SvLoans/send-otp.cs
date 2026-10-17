using System.Security.Cryptography;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Twilio;

// One reaction: a phone gets a six-digit code by SMS. The code lives only in {app}/otp/{phone}; a request
// delivered again reuses its ClientReference, so Twilio sends once.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
await foreach (var request in brain.On(app, "send-otp", brain.Stopping))
{
    var phone = request.Input.Trim();
    await Spike.Run(brain, key, "send-otp", request.RequestId, phone, async () =>
    {
        var sms = brain.Get<ITwilioSms>(Spike.Require(brain, "sms", "twilio"));
        if (phone.Length is < 8 or > 16 || phone[0] != '+' || !phone[1..].All(char.IsAsciiDigit))
        { throw new InvalidOperationException("The phone must be E.164, for example +51987654321."); }
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        await brain.Get<IText>($"{key}/otp/{phone}").Set(code);
        var message = await sms.Send(new(phone, $"Tu código SV es {code}", request.RequestId.ToString("N")));
        return ($"sid={message.Sid} to={message.To} status={message.Status} error={message.ErrorCode}", message.Status);
    });
}
