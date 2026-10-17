using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Twilio.Aspire.Hosting;

public sealed class TwilioModuleHosting : ModuleHosting<TwilioModule>
{
    public override void Configure(DigitalBrainBuilder brain)
        => new DigitalBrainModuleBuilder<TwilioModuleHosting>(brain).WithTwilio();
}
