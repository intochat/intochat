using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Google.Gmail;

public sealed class GmailModuleHosting : ModuleHosting<DigitalBrain.Google.Gmail.GmailModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
    }
}
