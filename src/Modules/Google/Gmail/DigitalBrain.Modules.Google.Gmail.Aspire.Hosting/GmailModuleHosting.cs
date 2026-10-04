using DigitalBrain.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Google.Gmail;

public sealed class GmailModuleHosting : ModuleHosting<DigitalBrain.Google.Gmail.GmailModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        var options = brain.GetModuleConfiguration("gmail").GetModuleOptions<GmailModuleOptions>("gmail");
        if (options.HostGmail) { new DigitalBrainModuleBuilder<DigitalBrain.Google.Gmail.GmailModuleHosting>(brain).WithGmail(); }
    }
}
